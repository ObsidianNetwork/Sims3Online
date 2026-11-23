//! Gate 6's comparison (card 56 §G gate 6): the Rust half's replies against the C# half's, on their decoded
//! shape only (reply kind, the ok or refusal token, the field names and count). Values that depend on the
//! game's moment (ticks, positions, new object ids) are not compared.
//!
//! Each Rust command is paired with the next C# row whose command line is the same once [`blank_for_pairing`] has
//! blanked it, so the controller's own commands in the C# capture are skipped. Besides correlation ids, that one
//! function blanks (card 56 §G gate 6, 4c970a49 row 208) (i) the ids a half created and (ii) the argument values
//! a half chose or read, at the positions its corpus marks (`#@blank`, [`blank_marks`]). A Rust refusal of a §E.3 verb passes
//! when it is that verb's named refusal and the C# half never sent the verb either; a Rust driver refusal of any
//! other verb fails, because gate 6 sends every other verb in both halves (card 56 §G gate 6, 23e5ff55). A verb
//! that expects a reply fails when either half got none, so a mod that answered nothing never passes. A send the
//! mod answered without running the handler's body (its refusal, or an OK for an absent target such as id 0) counts
//! toward `verbs_live` all the same, and [`body_skipped`] marks its row `body=skipped` (card 56 §G gate 6, c9a23656).
#![forbid(unsafe_code)]

use std::collections::HashSet;

use crate::error::BridgeError;
use crate::reply::decode_response;
use crate::verbs::{ReplyShape, VERB_TABLE};

/// A reply's decoded shape.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Shape {
    /// The line's first `|` field (`OK`, `ERROR`, `SNAPSHOT`, `DIAG_STATE_REPORT`, ...); empty for no reply.
    pub kind: String,
    /// `ok` when accepted; the refusal token when rejected; `none`, `undecoded` or `throws` otherwise.
    pub token: String,
    /// The field names, where the reply names them (a report's `key=value;...` keys, a snapshot's header).
    pub fields: Vec<String>,
    pub count: usize,
}

/// The refusal token of a rejection text: up to the first `:` or whitespace (`sim-motive-probe=none: ...`
/// gives `sim-motive-probe=none`).
pub fn refusal_token(rejection: &str) -> String {
    rejection.split(|c: char| c == ':' || c.is_whitespace()).next().unwrap_or("").to_string()
}

/// The names of a report's `key=value` fields, from its first line (rows after it vary with the moment).
fn report_fields(report: &str) -> Vec<String> {
    let first = report.lines().next().unwrap_or("");
    if first.is_empty() {
        return Vec::new();
    }
    first.split(';').map(|f| f.split_once('=').map_or("_", |(k, _)| k).to_string()).collect()
}

/// The decoded shape of one reply line.
pub fn shape(line: &str) -> Shape {
    let parts = line.split('|').count();
    let kind = line.split('|').next().unwrap_or("").to_string();
    if line.is_empty() {
        return Shape { kind, token: "none".into(), fields: Vec::new(), count: 0 };
    }
    match decode_response(line) {
        Ok(Some(r)) => {
            let token = if r.accepted { "ok".to_string() } else { refusal_token(r.rejection.as_deref().unwrap_or("")) };
            let fields = if let Some(report) = &r.probe_report {
                report_fields(report)
            } else if r.snapshot.is_some() {
                // The sim and object lists are the game's moment; the header is the shape.
                ["tick", "world", "save", "speed"].map(String::from).to_vec()
            } else {
                Vec::new()
            };
            let count = if fields.is_empty() { parts } else { fields.len() };
            Shape { kind, token, fields, count }
        }
        Ok(None) => Shape { kind, token: "undecoded".into(), fields: Vec::new(), count: parts },
        Err(_) => Shape { kind, token: "throws".into(), fields: Vec::new(), count: parts },
    }
}

/// One row of `rust_replies.jsonl` (`simlinkctl roundtrip`).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RustRow {
    /// The C# record name.
    pub verb: String,
    /// The command line sent (empty when refused).
    pub line: String,
    /// The reply line (empty when none came).
    pub reply: String,
    /// The Rust driver's named refusal, when it refused the verb.
    pub refused: Option<String>,
}

/// One `rust_replies.jsonl` row for a command the driver sent, and whether it counts as answered. A verb that
/// expects a reply and got none before the deadline is a failure row, `"timeout":true` (card 56 gate 6: a mod that
/// answers nothing must not reach `verbs_live`).
pub fn roundtrip_row(verb: &str, line: &str, reply: &str, expects_reply: bool) -> (String, bool) {
    use crate::corpus::json_str;
    let timed_out = expects_reply && reply.is_empty();
    let row = format!(
        "{{\"verb\":{},\"line\":{},\"reply\":{},\"dump\":{}{}}}",
        json_str(verb),
        json_str(line),
        json_str(reply),
        json_str(&crate::reply::dump_line(reply)),
        if timed_out { ",\"timeout\":true" } else { "" }
    );
    (row, !timed_out)
}

/// One row of `csharp_replies.txt` (`scripts/bridge_record_replies.py`): a command row, or with an empty `command`
/// a host frame no command claimed (`wire` is then the line's first `|` field).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CsRow {
    pub wire: String,
    pub command: String,
    pub reply: String,
}

/// One `live_roundtrip.jsonl` row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LiveResult {
    pub verb: String,
    pub refused: bool,
    pub pass: bool,
    pub note: String,
    /// The mod answered this send without running the handler's body ([`body_skipped`], card 56 c9a23656).
    pub body_skipped: bool,
}

/// A corpus `#@blank <WIRE> <field>[,<field>...]` directive: the `|` fields of that verb's command line (0 is the
/// verb, 1 the correlation id) that carry a value the half chose or read, such as its own scratch save name or a
/// value it read first and restores (card 56 §G gate 6, 4c970a49 row 208 (ii)).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BlankMark {
    pub wire: String,
    pub fields: Vec<usize>,
}

/// The `#@blank` directives of a corpus file (comment lines, which [`crate::corpus::rows`] skips).
pub fn blank_marks(corpus: &str) -> Result<Vec<BlankMark>, String> {
    corpus
        .lines()
        .filter_map(|l| l.strip_prefix("#@blank "))
        .map(|rest| {
            let bad = || format!("#@blank {rest}: expected `#@blank <WIRE> <field>[,<field>...]`, fields 2 or more");
            let parts: Vec<&str> = rest.split_whitespace().collect();
            let [wire, list] = parts[..] else { return Err(bad()) };
            let fields = list.split(',').map(|f| f.parse::<usize>().map_err(|_| bad())).collect::<Result<Vec<_>, _>>()?;
            if fields.iter().any(|&f| f < 2) {
                return Err(bad());
            }
            Ok(BlankMark { wire: wire.to_string(), fields })
        })
        .collect()
}

/// The ids a reply says its half created (card 56 §G gate 6, 4c970a49 row 208 (i)): the reply fields that name a
/// Sim, object, dialog or save the half itself made. Today those are `SPAWN_SIM`'s `local=` when its report says
/// `result=spawned` (not `existed`), and `SESSION_READY`'s save field. A shown dialog's id is the half's own
/// `SHOW_DIALOG` request id, an argument it chose, so the corpus marks it instead.
pub fn created_ids(reply: &str) -> Vec<String> {
    let mut out = Vec::new();
    let fields: Vec<&str> = reply.split('|').collect();
    if fields[0] == "SESSION_READY" && fields.len() > 2 && !fields[2].is_empty() {
        out.push(fields[2].to_string());
    }
    if let Ok(Some(r)) = decode_response(reply) {
        let first = r.probe_report.as_deref().unwrap_or("").lines().next().unwrap_or("").to_string();
        let pairs: Vec<(&str, &str)> = first.split(';').filter_map(|f| f.trim().split_once('=')).collect();
        if pairs.contains(&("result", "spawned")) {
            out.extend(pairs.iter().filter(|(k, _)| *k == "local").map(|(_, v)| v.to_string()));
        }
    }
    out
}

fn is_guid(field: &str) -> bool {
    field.len() == 32 && field.bytes().all(|b| b.is_ascii_hexdigit())
}

/// The digit runs of a field that are ids the half created, blanked (`111:5` becomes `<created>:5`).
fn blank_created(field: &str, created: &HashSet<String>) -> String {
    if created.contains(field) {
        return "<created>".to_string();
    }
    let mut out = String::with_capacity(field.len());
    let mut run = String::new();
    let flush = |run: &mut String, out: &mut String| {
        out.push_str(if created.contains(run.as_str()) { "<created>" } else { run.as_str() });
        run.clear();
    };
    for c in field.chars() {
        if c.is_ascii_digit() {
            run.push(c);
        } else {
            flush(&mut run, &mut out);
            out.push(c);
        }
    }
    flush(&mut run, &mut out);
    out
}

/// The one pairing key (card 56 §G gate 6, 4c970a49 row 208, "Blanked for pairing"): a command line with its
/// correlation ids blanked (`<id>`), (ii) the fields its corpus marks for this verb blanked (`<half>`: the values a
/// half chose or read), and (i) every id this half created blanked wherever an argument carries it (`<created>`).
pub fn blank_for_pairing(line: &str, marks: &[BlankMark], created: &HashSet<String>) -> String {
    let wire = line.split('|').next().unwrap_or("");
    let marked: Vec<usize> = marks.iter().filter(|m| m.wire == wire).flat_map(|m| m.fields.iter().copied()).collect();
    line.split('|')
        .enumerate()
        .map(|(i, f)| {
            if is_guid(f) {
                "<id>".to_string()
            } else if marked.contains(&i) {
                "<half>".to_string()
            } else if i == 0 {
                f.to_string()
            } else {
                blank_created(f, created)
            }
        })
        .collect::<Vec<_>>()
        .join("|")
}

/// The id no Sim, object or household has: a verb aimed at it changes nothing (the `ABSENT` argument of
/// `scripts/card56_corpus.py`), so the handler looks it up and returns before its body.
pub const ABSENT_TARGET: &str = "0";

/// The command field that names the Sim, object or household the verb acts on: its first argument (field 0 is the
/// wire verb, 1 the correlation id), the part every mod handler reads its target from (`command.Parts[2]`).
const TARGET_FIELD: usize = 2;

/// Whether a send was answered without running the handler's body (card 56 §G gate 6, c9a23656 row 208), read from
/// the command line and the reply alone: the mod's own refusal (any line the decoder reads as not accepted, so a
/// rejection text such as `Sim not found: 0` or a role check), or a no-op `OK` for an absent target (a bare
/// `OK|corr` answering a command whose target id is [`ABSENT_TARGET`], which nothing in the world has, so the
/// handler found nothing to act on). A reply that is neither is taken as an answer after the body ran: a read's
/// report, or a set that changed something. Where the reply alone cannot tell the two apart for a verb, the lane
/// report lists that verb under QUESTIONS rather than guess.
pub fn body_skipped(command: &str, reply: &str) -> bool {
    if reply.is_empty() {
        return false;
    }
    match decode_response(reply) {
        Ok(Some(r)) if !r.accepted => true,
        Ok(Some(r)) => {
            r.probe_report.is_none()
                && r.snapshot.is_none()
                && reply.split('|').count() == 2
                && command.split('|').nth(TARGET_FIELD) == Some(ABSENT_TARGET)
        }
        _ => false,
    }
}

fn result(verb: &str, refused: bool, pass: bool, note: String) -> LiveResult {
    LiveResult { verb: verb.to_string(), refused, pass, note, body_skipped: false }
}

/// Pair each Rust row with a C# row and compare the replies, with no corpus marks.
pub fn live_diff(rust: &[RustRow], cs: &[CsRow]) -> Vec<LiveResult> {
    live_diff_marked(rust, cs, &[])
}

/// [`live_diff`] with the corpus's `#@blank` marks: each row pairs on [`blank_for_pairing`], each half's created ids
/// taken from that half's own replies.
pub fn live_diff_marked(rust: &[RustRow], cs: &[CsRow], marks: &[BlankMark]) -> Vec<LiveResult> {
    let rust_created: HashSet<String> = rust.iter().flat_map(|r| created_ids(&r.reply)).collect();
    let cs_created: HashSet<String> = cs.iter().filter(|c| !c.command.is_empty()).flat_map(|c| created_ids(&c.reply)).collect();
    let mut cursor = 0;
    let mut out = Vec::with_capacity(rust.len());
    for r in rust {
        let row = VERB_TABLE.iter().find(|v| v.record == r.verb);
        if let Some(name) = &r.refused {
            let wire = row.map_or("", |v| v.wire);
            if row.is_some_and(|v| v.refused) {
                let expected = BridgeError::RefusedLegacyVerb(wire).name();
                // An empty-command row is a host frame no command claimed, not a send.
                let sent_by_csharp = cs.iter().any(|c| !c.command.is_empty() && c.wire == wire);
                let pass = *name == expected && !sent_by_csharp;
                let note = if *name != expected {
                    format!("refusal {name}, expected {expected}")
                } else if sent_by_csharp {
                    format!("the C# half sent {wire}, a verb neither half sends")
                } else {
                    String::new()
                };
                out.push(result(&r.verb, true, pass, note));
                continue;
            }
            out.push(result(&r.verb, true, false, format!("driver refusal {name}: {wire} is sent in both halves")));
            continue;
        }
        let key = blank_for_pairing(&r.line, marks, &rust_created);
        let Some(at) = (cursor..cs.len()).find(|&i| blank_for_pairing(&cs[i].command, marks, &cs_created) == key) else {
            out.push(result(&r.verb, false, false, format!("no C# row sends {key}")));
            continue;
        };
        cursor = at + 1;
        let expects_reply = !row.is_some_and(|v| matches!(v.reply, ReplyShape::None));
        let missing = match (r.reply.is_empty(), cs[at].reply.is_empty()) {
            (true, true) => Some("either half"),
            (true, false) => Some("the Rust half"),
            (false, true) => Some("the C# half"),
            (false, false) => None,
        };
        if let (true, Some(half)) = (expects_reply, missing) {
            out.push(result(&r.verb, false, false, format!("no reply in {half}")));
            continue;
        }
        let (ours, theirs) = (shape(&r.reply), shape(&cs[at].reply));
        let pass = ours == theirs;
        let note = if pass { String::new() } else { format!("rust {ours:?} csharp {theirs:?}") };
        // Both halves answer through the same mod, so either half's skipped answer marks the row; a pair that
        // disagrees on this differs in shape too, and fails for the reason above (card 56 c9a23656).
        let body_skipped = body_skipped(&r.line, &r.reply) || body_skipped(&cs[at].command, &cs[at].reply);
        out.push(LiveResult { verb: r.verb.clone(), refused: false, pass, note, body_skipped });
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const R: &str = "0123456789abcdef0123456789abcdef";
    const C: &str = "fedcba9876543210fedcba9876543210";
    /// The three verbs card 56 gate 6 (c9a23656) sends with literal arguments an empty list or a null writes as `-`.
    const CARD_LITERALS: [&str; 3] = ["AnswerDialog", "SetSimScope", "SetAuthority"];

    fn rust(verb: &str, line: &str, reply: &str) -> RustRow {
        RustRow { verb: verb.into(), line: line.into(), reply: reply.into(), refused: None }
    }

    fn refused(verb: &str, name: &str) -> RustRow {
        RustRow { verb: verb.into(), refused: Some(name.into()), ..RustRow::default() }
    }

    fn cs(command: &str, reply: &str) -> CsRow {
        CsRow { wire: command.split('|').next().unwrap_or("").into(), command: command.into(), reply: reply.into() }
    }

    fn passes(rust: &[RustRow], cs: &[CsRow]) -> Vec<bool> {
        live_diff(rust, cs).iter().map(|r| r.pass).collect()
    }

    fn passes_marked(rust: &[RustRow], cs: &[CsRow], marks: &[BlankMark]) -> Vec<bool> {
        live_diff_marked(rust, cs, marks).iter().map(|r| r.pass).collect()
    }

    fn spawned(id: &str, local: u64) -> String {
        format!("AUTONOMY_PROBE|{id}|ok|{}", crate::text::encode_text(&format!("result=spawned; local={local}; name=X")))
    }

    #[test]
    fn each_halfs_own_scratch_save_and_restored_values_pair_through_the_corpus_marks() {
        // Card 56 gate 6 (4c970a49 row 208, (ii)): each half saves under its own scratch name and restores the values
        // it read, so those arguments differ between the halves; the corpus marks them for blanking.
        let save = |id: &str, name: &str| format!("SAVE_GAME|{id}|{}", crate::text::encode_text(name));
        let r = [
            rust("SaveGame", &save(R, "card56-gate6-rust-1.sims3"), &format!("OK|{R}")),
            rust("SetWorldTime", &format!("SET_WORLD_TIME|{R}|3|14|26"), &format!("OK|{R}")),
        ];
        let c = [
            cs(&save(C, "card56-gate6-csharp-1.sims3"), &format!("OK|{C}")),
            cs(&format!("SET_WORLD_TIME|{C}|3|14|25"), &format!("OK|{C}")),
        ];
        assert_eq!(passes(&r, &c), [false, false], "unmarked, the halves' own values never pair");
        let marks = blank_marks("# c\n#@blank SAVE_GAME 2\n#@blank SET_WORLD_TIME 2,3,4\nPing\tx\n").unwrap();
        assert_eq!(passes_marked(&r, &c, &marks), [true, true]);
        // A mark blanks only its own verb's fields: an unmarked difference still fails.
        let other = [rust("SetActiveSim", &format!("SET_ACTIVE_SIM|{R}|7"), &format!("OK|{R}"))];
        let theirs = [cs(&format!("SET_ACTIVE_SIM|{C}|8"), &format!("OK|{C}"))];
        assert_eq!(passes_marked(&other, &theirs, &marks), [false]);
    }

    #[test]
    fn a_later_command_carrying_an_id_its_half_created_pairs() {
        // (i) and the reading of (ii): an argument carrying an id the half itself created is blanked.
        let gone = |id: &str| format!("AUTONOMY_PROBE|{id}|ok|{}", crate::text::encode_text("result=despawned"));
        let r = [
            rust("SpawnSim", &format!("SPAWN_SIM|{R}|9|5|1|2|3"), &spawned(R, 111)),
            rust("DespawnSim", &format!("DESPAWN_SIM|{R}|111|5"), &gone(R)),
        ];
        let c = [
            cs(&format!("SPAWN_SIM|{C}|9|5|1|2|3"), &spawned(C, 222)),
            cs(&format!("DESPAWN_SIM|{C}|222|5"), &gone(C)),
        ];
        assert_eq!(passes(&r, &c), [true, true]);
        // An id neither half created is compared.
        let r = [r[0].clone(), rust("DespawnSim", &format!("DESPAWN_SIM|{R}|333|5"), &gone(R))];
        assert_eq!(passes(&r, &c), [true, false]);
    }

    #[test]
    fn created_ids_are_the_reply_fields_naming_what_the_half_made() {
        assert_eq!(created_ids(&spawned(R, 111)), ["111"]);
        let existed = format!("AUTONOMY_PROBE|{R}|ok|{}", crate::text::encode_text("result=existed; local=111"));
        assert!(created_ids(&existed).is_empty(), "a Sim that already existed was not made by the half");
        assert_eq!(created_ids(&format!("SESSION_READY|{R}|414243|57|9")), ["414243"]);
        assert!(created_ids(&format!("OK|{R}")).is_empty());
        assert!(created_ids("").is_empty());
    }

    #[test]
    fn blank_for_pairing_blanks_correlation_ids_marked_values_and_created_ids() {
        let marks = blank_marks("#@blank SAVE_GAME 2").unwrap();
        let created: std::collections::HashSet<String> = ["111".to_string()].into();
        assert_eq!(blank_for_pairing(&format!("SAVE_GAME|{R}|414243"), &marks, &created), "SAVE_GAME|<id>|<half>");
        assert_eq!(
            blank_for_pairing(&format!("SET_SIM_ALIASES|{R}|111:5,1111:6"), &marks, &created),
            "SET_SIM_ALIASES|<id>|<created>:5,1111:6"
        );
        assert_eq!(blank_for_pairing(&format!("PING|{R}"), &[], &Default::default()), "PING|<id>");
    }

    #[test]
    fn blank_marks_reads_the_directives_and_refuses_a_bad_one() {
        let marks = blank_marks("#@blank SET_WORLD_TIME 2,3,4\n# other\nPing\tx\n").unwrap();
        assert_eq!(marks, [BlankMark { wire: "SET_WORLD_TIME".into(), fields: vec![2, 3, 4] }]);
        for bad in ["#@blank SAVE_GAME", "#@blank SAVE_GAME x", "#@blank SAVE_GAME 1", "#@blank SAVE_GAME 2 3"] {
            assert!(blank_marks(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn the_live_corpus_sample_parses_encodes_and_crosses_the_listener() {
        // scripts/card56_corpus.py's output for its tests' fake paused world, the Rust half (scripts/
        // card56_corpus_test.py checks it is): a row for every declared verb, no `#@unsent` line left, and six rows
        // for SetWorldTime where its one row was (card 56 §G gate 6, c9a23656 row 208).
        let text = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/fixtures/live_corpus_sample.txt"))
            .expect("read the live corpus sample");
        assert!(!blank_marks(&text).unwrap().is_empty());
        assert_eq!(text.lines().filter(|l| l.starts_with("#@unsent ")).count(), 0, "the ruling sends every verb");
        let mut verbs = Vec::new();
        for row in crate::corpus::rows(&text) {
            let cmd = crate::corpus::parse_command(&row).unwrap_or_else(|e| panic!("{row:?}: {e}"));
            verbs.push(cmd.record());
            match crate::verbs::encode(&cmd) {
                // bridgectl.py admin_request: the space-split listener carries no space and no non-ASCII, and it
                // reads a bare `-` token as an empty wire part, so a literal `-` part goes as its `\-` escape. The
                // three verbs whose arguments the card fixes as an empty list or a null are the only lines with a
                // `-` field; `the_cards_three_literals_encode_a_lone_dash_that_the_escape_carries` pins them.
                Ok(line) if CARD_LITERALS.contains(&cmd.record()) => {
                    assert!(line.split('|').skip(2).any(|a| a == "-"), "{line}: only the `-` fields are exempt");
                }
                Ok(line) => assert!(
                    line.split('|').skip(2).all(|a| a != "-" && a.is_ascii() && !a.chars().any(char::is_whitespace)),
                    "{line}"
                ),
                Err(e) => assert!(cmd.row().refused, "{}: {e:?}", cmd.record()),
            }
        }
        let unsent = text.lines().filter_map(|l| l.strip_prefix("#@unsent ")).map(|l| l.split(' ').next().unwrap_or(""));
        let mut named: Vec<&str> = verbs.iter().copied().chain(unsent).collect();
        named.sort_unstable();
        named.dedup();
        let mut table: Vec<&str> = VERB_TABLE.iter().map(|r| r.record).collect();
        table.sort_unstable();
        assert_eq!(named, table, "every declared verb has a row, or is listed unsent");
        // SetWorldTime is the one verb with more than one row: the ruling's six take the place of its single row,
        // two of them DIAG_STATE reads, so the 60 verbs of a paused half land in 65 rows.
        let rows_of = |want: &str| verbs.iter().filter(|v| **v == want).count();
        assert_eq!(verbs.len(), 65, "60 verbs, six rows where SetWorldTime had one");
        assert_eq!(rows_of("SetWorldTime"), 2, "set T plus one game minute, then set T");
        assert_eq!(rows_of("DiagState"), 3, "the sequence's two reads beside the verb's own row");
        assert_eq!(rows_of("SetGameSpeed"), 3, "the verb's own restore, the pause, the sequence's restore");
        assert_eq!(&verbs[..7],
                   ["SetGameSpeed", "DiagState", "SetGameSpeed", "DiagState", "SetWorldTime", "SetWorldTime",
                    "SetGameSpeed"],
                   "the six rows sit where the single SetWorldTime row sat");
        // Card 56 gate 6 (c9a23656 row 208, "Order within a half"): the other verbs, then LOAD_SAVE, then
        // LOAD_SAVE_MENU, then the three the ruling sends with literal args, then DISCONNECT last.
        assert_eq!(&verbs[verbs.len() - 7..],
                   ["SaveGame", "LoadSave", "LoadSaveViaMenu", "AnswerDialog", "SetSimScope", "SetAuthority",
                    "Disconnect"]);
    }

    #[test]
    fn the_cards_three_literals_encode_a_lone_dash_that_the_escape_carries() {
        // Card 56 gate 6 (c9a23656 row 208) fixes the arguments: AnswerDialog(RequestId = 0, Answer = null),
        // SetSimScope(LotIds = [], SimObjectIds = []), SetAuthority(Mode = Observe, OwnedSimObjectIds = []).
        // The codec writes a null answer and an empty id list as a lone `-` (src/verbs.rs, SimScopeWire.Empty),
        // which the Rust half sends verbatim into the ctl slot. On the C# half the same line goes through the
        // controller's space-split admin listener, which reads a bare `-` token as an *empty* part: the lane's Q2,
        // ruled on 2026-11-01 (Main) by an escape the listener understands. `\-` in a request token means a
        // literal `-` argument (`DedicatedServer/Network/BridgeAdminListener.cs:191-199`, and
        // `BridgeAdminListener.ADashIsAnEmptyPartAndAnEscapedDashIsLiteral`), and `scripts/bridgectl.py`
        // admin_request writes a `-` field that way, so `EncodeRawCommand` records the line below byte for byte and
        // the two halves pair on it instead of `|-|-` against `||`.
        let lit = |record: &str, args: &[&str]| -> String {
            let mut row = vec![record.to_string(), R.to_string()];
            row.extend(args.iter().map(|a| a.to_string()));
            crate::verbs::encode(&crate::corpus::parse_command(&row).unwrap()).unwrap()
        };
        assert_eq!(lit("AnswerDialog", &["0", "~"]), format!("DIALOG_ANSWER|{R}|0|-"));
        assert_eq!(lit("SetSimScope", &["", ""]), format!("SET_SIM_SCOPE|{R}|-|-"));
        assert_eq!(lit("SetAuthority", &["Observe", ""]), format!("SET_AUTHORITY|{R}|observe|-"));
        assert_eq!(lit("ProbeSimState", &["1,2"]), format!("PROBE_SIM_STATE|{R}|1,2"), "a non-empty list is unchanged");
    }

    #[test]
    fn a_refusal_or_a_no_op_ok_for_an_absent_target_is_a_send_whose_body_was_skipped() {
        // Card 56 gate 6 (c9a23656 row 208): the test is the refusal token, or an OK that answers a target id
        // no Sim or object has. Everything else is taken as an answer after the handler's body ran.
        let refusal = format!("ERROR|{R}|{}", crate::text::encode_text("Sim not found: 0"));
        assert!(body_skipped(&format!("SET_ACTIVE_SIM|{R}|0"), &refusal), "the mod's own refusal");
        let role = format!("ERROR|{R}|{}", crate::text::encode_text("SHOW_DIALOG is client-role only and needs ..."));
        assert!(body_skipped(&format!("SHOW_DIALOG|{R}|1|1003|{}|0", crate::text::encode_text("TwoButton")), &role),
                "the role check on the server");
        assert!(body_skipped(&format!("LEAVE_CONVERSATION|{R}|0"), &format!("OK|{R}")), "a no-op OK for id 0");
        assert!(!body_skipped(&format!("SET_ACTIVE_SIM|{R}|1003"), &format!("OK|{R}")), "a live target ran the body");
        let report = format!("AUTONOMY_PROBE|{R}|ok|{}", crate::text::encode_text("result=missing"));
        assert!(!body_skipped(&format!("EXPORT_SIM_DESCRIPTION|{R}|0"), &report),
                "a report is not the bare OK this test names: the lane report lists it under QUESTIONS");
        assert!(!body_skipped(&format!("SET_ACTIVE_SIM|{R}|0"), ""), "no reply answers nothing");
        assert!(!body_skipped(&format!("DIAG_STATE|{R}"), &format!("DIAG_STATE_REPORT|{R}|ok|{}",
                crate::text::encode_text("role=server;tick=1"))), "a read reply is an answer after the body");
    }

    #[test]
    fn live_diff_marks_a_skipped_body_and_leaves_an_answered_send_unmarked() {
        let refusal = |id: &str| format!("ERROR|{id}|{}", crate::text::encode_text("Sim not found: 0"));
        let r = [rust("SetActiveSim", &format!("SET_ACTIVE_SIM|{R}|0"), &refusal(R))];
        let c = [cs(&format!("SET_ACTIVE_SIM|{C}|0"), &refusal(C))];
        let got = live_diff(&r, &c);
        assert!(got[0].pass, "the same refusal in both halves is the same shape");
        assert!(got[0].body_skipped, "gate 6 counts the send and marks the row (c9a23656)");
        let ran = [rust("SetActiveSim", &format!("SET_ACTIVE_SIM|{R}|1003"), &format!("OK|{R}"))];
        let theirs = [cs(&format!("SET_ACTIVE_SIM|{C}|1003"), &format!("OK|{C}"))];
        let got = live_diff(&ran, &theirs);
        assert!(got[0].pass && !got[0].body_skipped, "a send whose body ran is not marked");
        // One half refused, the other answered: the shapes differ, so the row fails; the mark says why to look.
        let split = [rust("LeaveConversation", &format!("LEAVE_CONVERSATION|{R}|0"), &format!("OK|{R}"))];
        let theirs = [cs(&format!("LEAVE_CONVERSATION|{C}|0"), &refusal(C))];
        let got = live_diff(&split, &theirs);
        assert!(!got[0].pass && got[0].body_skipped, "{:?}", got[0]);
        // A verb neither half sent (the driver's named refusal) is not a send, so it is never marked.
        let named = [refused("ApplyInteractions", "refused_legacy_verb:APPLY_INTERACTIONS")];
        let got = live_diff(&named, &[]);
        assert!(got[0].pass && !got[0].body_skipped, "{:?}", got[0]);
    }

    #[test]
    fn ticks_positions_and_report_values_are_not_compared() {
        let snap = |id: &str, tick: u32, x: &str| format!("SNAPSHOT|{id}|{tick}|57|58|Normal|1|7:41:9:{x}:0:2:-:-:-:5:3:99|0");
        let diag = |id: &str, tick: u32| format!("DIAG_STATE_REPORT|{id}|ok|{}", crate::text::encode_text(&format!("role=server;tick={tick}")));
        let r = [
            rust("RequestSnapshot", &format!("REQUEST_SNAPSHOT|{R}"), &snap(R, 42, "1.5")),
            rust("DiagState", &format!("DIAG_STATE|{R}"), &diag(R, 4521)),
        ];
        let c = [cs(&format!("REQUEST_SNAPSHOT|{C}"), &snap(C, 977, "3.25")), cs(&format!("DIAG_STATE|{C}"), &diag(C, 9))];
        assert_eq!(passes(&r, &c), [true, true]);
    }

    #[test]
    fn a_different_kind_token_or_field_set_fails() {
        let diag = |id: &str, body: &str| format!("DIAG_STATE_REPORT|{id}|ok|{}", crate::text::encode_text(body));
        let r = [
            rust("Ping", &format!("PING|{R}"), &format!("OK|{R}")),
            rust("DiagState", &format!("DIAG_STATE|{R}"), &diag(R, "role=server;tick=1")),
            rust("SimMotiveProbe", &format!("SIM_MOTIVE_PROBE|{R}|7"), &format!("SIM_MOTIVE_PROBE|{R}|ok|6869")),
        ];
        let c = [
            cs(&format!("PING|{C}"), &format!("ERROR|{C}|414243")),
            cs(&format!("DIAG_STATE|{C}"), &diag(C, "role=server")),
            cs(&format!("SIM_MOTIVE_PROBE|{C}|7"), &format!("SIM_MOTIVE_PROBE|{C}|none|6869")),
        ];
        assert_eq!(passes(&r, &c), [false, false, false]);
    }

    #[test]
    fn no_reply_in_either_half_passes_only_for_a_verb_without_a_reply() {
        let r = [
            rust("Ping", &format!("PING|{R}"), ""),
            rust("DiagState", &format!("DIAG_STATE|{R}"), ""),
            rust("Disconnect", &format!("DISCONNECT|{R}"), ""),
        ];
        let c = [cs(&format!("PING|{C}"), ""), cs(&format!("DIAG_STATE|{C}"), ""), cs(&format!("DISCONNECT|{C}"), "")];
        let results = live_diff(&r, &c);
        assert_eq!(results.iter().map(|x| x.pass).collect::<Vec<_>>(), [false, false, true]);
        assert!(results[0].note.contains("no reply"), "{:?}", results[0]);
    }

    #[test]
    fn a_roundtrip_timeout_is_a_failure_row() {
        let (row, ok) = roundtrip_row("Ping", &format!("PING|{R}"), "", true);
        assert!(!ok);
        assert!(row.contains("\"timeout\":true"), "{row}");
        let (row, ok) = roundtrip_row("Disconnect", &format!("DISCONNECT|{R}"), "", false);
        assert!(ok && !row.contains("timeout"), "{row}");
        let (row, ok) = roundtrip_row("Ping", &format!("PING|{R}"), &format!("OK|{R}"), true);
        assert!(ok && !row.contains("timeout"), "{row}");
        assert!(row.starts_with("{\"verb\":\"Ping\",\"line\":"), "{row}");
    }

    #[test]
    fn a_rust_driver_refusal_of_a_verb_outside_e3_fails() {
        // Card 56 gate 6 (23e5ff55): every non-§E.3 verb is sent in both halves, so a driver refusal fails even
        // against a C# refusal with the same token.
        let r = [refused("Ping", "frame_too_large")];
        assert_eq!(passes(&r, &[cs(&format!("PING|{C}"), &format!("OK|{C}"))]), [false]);
        let same = format!("ERROR|{C}|{}", crate::text::encode_text("frame_too_large: over the slot"));
        assert_eq!(passes(&r, &[cs(&format!("PING|{C}"), &same)]), [false]);
        assert_eq!(passes(&r, &[]), [false]);
    }

    #[test]
    fn empty_command_rows_are_host_frames_not_sends() {
        // Card 56 gate 6 (1b8fbc27): `<first | field>\t\t<host line>` rows are skipped when pairing, and an
        // unclaimed host line never counts as the C# half sending a verb.
        let unclaimed = |line: &str| CsRow { wire: line.split('|').next().unwrap_or("").into(), command: String::new(), reply: line.into() };
        let named = [refused("ApplyInteractions", "refused_legacy_verb:APPLY_INTERACTIONS")];
        assert_eq!(passes(&named, &[unclaimed(&format!("APPLY_INTERACTIONS|{C}|ok"))]), [true]);
        let r = [rust("Ping", &format!("PING|{R}"), &format!("OK|{R}"))];
        let c = [unclaimed("EVENT|InteractionStarted|1|2"), unclaimed(&format!("OK|{C}")), cs(&format!("PING|{C}"), &format!("OK|{C}"))];
        assert_eq!(passes(&r, &c), [true]);
    }

    #[test]
    fn an_e3_refusal_passes_only_as_the_named_refusal_never_sent() {
        // A §E.3 verb: never sent by either half, answered by the driver's named refusal.
        let named = [refused("ApplyInteractions", "refused_legacy_verb:APPLY_INTERACTIONS")];
        assert_eq!(passes(&named, &[]), [true]);
        let wrong = [refused("ApplyInteractions", "frame_too_large")];
        assert_eq!(passes(&wrong, &[]), [false]);
        // The C# half sent a §E.3 verb: the halves did not run the same list.
        let sent = [cs(&format!("APPLY_INTERACTIONS|{C}"), &format!("OK|{C}"))];
        assert_eq!(passes(&named, &sent), [false]);
    }

    #[test]
    fn rows_align_on_the_command_line_skipping_the_controllers_own_commands() {
        let r = [
            rust("Ping", &format!("PING|{R}"), &format!("OK|{R}")),
            rust("SetGameSpeed", &format!("SET_SPEED|{R}|Fast"), &format!("OK|{R}")),
        ];
        let c = [
            cs(&format!("REQUEST_SNAPSHOT|{C}"), &format!("ERROR|{C}|414243")),
            cs(&format!("PING|{C}"), &format!("OK|{C}")),
            cs(&format!("SET_SPEED|{C}|Fast"), &format!("OK|{C}")),
        ];
        assert_eq!(passes(&r, &c), [true, true]);
        // A command the C# half never sent fails rather than pairing with the next row.
        let missing = [cs(&format!("PING|{C}"), &format!("OK|{C}"))];
        let results = live_diff(&r, &missing);
        assert_eq!(results.iter().map(|x| x.pass).collect::<Vec<_>>(), [true, false]);
        assert!(results[1].note.contains("no C# row"), "{:?}", results[1]);
    }
}
