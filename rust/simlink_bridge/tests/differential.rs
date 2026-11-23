//! Gates 4 and 5 inside `cargo test`: the Rust encoder and decoders against the oracle outputs committed in
//! `fixtures/` (produced by `oracle/` from `BridgeLineCodec`; `tools/run_offline_gates.py` regenerates them and
//! fails when the committed copies are stale).
#![forbid(unsafe_code)]

use std::collections::BTreeSet;

use simlink_bridge::corpus::{parse_command, rows};
use simlink_bridge::reply::dump_line;
use simlink_bridge::text::{format_f2, format_r};
use simlink_bridge::verbs::{encode, VERB_TABLE};

fn fixture(name: &str) -> String {
    std::fs::read_to_string(format!("{}/fixtures/{name}", env!("CARGO_MANIFEST_DIR"))).expect(name)
}

#[test]
fn every_verb_encodes_byte_identical_to_bridge_line_codec_or_is_refused() {
    let corpus = rows(&fixture("verb_corpus.txt"));
    let oracle = rows(&fixture("oracle_verb_lines.txt"));
    assert_eq!(corpus.len(), oracle.len());
    let mut covered = BTreeSet::new();
    for (row, o) in corpus.iter().zip(&oracle) {
        let cmd = parse_command(row).unwrap_or_else(|e| panic!("{row:?}: {e}"));
        assert_eq!(cmd.record(), o[0]);
        covered.insert(cmd.record());
        match encode(&cmd) {
            Ok(line) => assert_eq!(line, o[1], "{}", cmd.record()),
            Err(refusal) => {
                assert!(cmd.row().refused, "{} refused unexpectedly", cmd.record());
                assert_eq!(o[1], "<refused>");
                assert_eq!(refusal.name(), format!("refused_legacy_verb:{}", cmd.row().wire));
            }
        }
    }
    let all: BTreeSet<&str> = VERB_TABLE.iter().map(|r| r.record).collect();
    assert_eq!(covered, all, "the corpus must cover every verb");
}

#[test]
fn every_reply_line_decodes_to_the_oracle_fields() {
    let corpus = rows(&fixture("reply_corpus.txt"));
    let oracle_text = fixture("oracle_reply_dumps.txt");
    let oracle: Vec<&str> = oracle_text.lines().filter(|l| !l.trim().is_empty()).collect();
    assert_eq!(corpus.len(), oracle.len());
    for (row, o) in corpus.iter().zip(&oracle) {
        let (tag, theirs) = o.split_once('\t').expect("tagged");
        assert_eq!(tag, row[0]);
        assert_eq!(dump_line(&row[1]), theirs, "line {:?}", row[1]);
    }
}

#[test]
fn float_formatting_matches_dotnet_r_and_f2() {
    for r in rows(&fixture("oracle_float_lines.txt")) {
        let v = f32::from_bits(u32::from_str_radix(&r[0], 16).unwrap());
        assert_eq!(format_r(v), r[1], "R of {}", r[0]);
        assert_eq!(format_f2(v), r[2], "F2 of {}", r[0]);
    }
}
