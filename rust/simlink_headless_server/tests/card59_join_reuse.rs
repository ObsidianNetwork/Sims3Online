#![forbid(unsafe_code)]

use simlink_headless_server::join_reuse::*;
use std::time::Duration;

const HOME_SCOPE: &str = "h:1001|l:2001";

fn reply(scope: &str, objects: &str, inventories: &str, relationships: &str, ms: &str) -> String {
    format!("scope={scope};objects={objects};inventories={inventories};relationships={relationships};ms={ms}")
}

fn default_reply() -> String {
    reply(HOME_SCOPE, "54:6fba936a7d857f58", "28:230e39fcf3806d36", "3:f90efb809f3ca050", "2.33")
}

fn fingerprint(report: &str) -> WorldFingerprint {
    WorldFingerprint::parse(Some(report)).unwrap()
}

// Mirrors DedicatedServer.Tests/JoinWorldReuseTests.cs, including each refused reply.
#[test]
fn parses_the_mods_reply_and_refuses_the_rest() {
    let parsed = fingerprint(&default_reply());
    assert_eq!(parsed.scope, HOME_SCOPE);
    assert_eq!(parsed.objects, FingerprintPart { count: 54, hash: "6fba936a7d857f58".into() });
    assert_eq!(parsed.inventories.count, 28);
    assert_eq!(parsed.relationships.count, 3);
    assert!((parsed.scan_ms - 2.33).abs() < 1e-9);

    for (report, expected) in [
        (None, "no fingerprint taken"),
        (Some("none"), "no fingerprint taken"),
        (Some("error=NullReferenceException:Object reference not set"), "NullReferenceException:Object reference not set"),
        (Some("status;replayDrivers=0;borrowedNow=0;on=0"), "not a fingerprint"),
        (Some("scope=h:1|l:2;objects=54:aa;inventories=28:bb"), "not a fingerprint"),
        (Some("scope=h:1|l:2;objects=x:aa;inventories=28:bb;relationships=3:cc"), "not a fingerprint"),
    ] {
        let error = WorldFingerprint::parse(report).unwrap_err();
        assert!(error.contains(expected), "{report:?}: {error}");
    }
}

#[test]
fn hits_only_when_nothing_changed() {
    assert_eq!(JOIN_SCAN_TIMEOUT, Duration::from_secs(3));
    let saved = fingerprint(&default_reply());
    let mut now = saved.clone();
    now.scan_ms = 1.0;
    let hit = decide(Some(&saved), Some(&now), None);
    assert!(hit.reuse);
    assert!(hit.misses.is_empty());
    assert_eq!(hit.detail, "nothing a joiner needs changed since the last save");

    let none = decide(None, Some(&saved), None);
    assert!(!none.reuse);
    assert_eq!(none.misses, [NO_FINGERPRINT_YET]);
    assert_eq!(none.detail, "the last save has no fingerprint");

    let failed = decide(Some(&saved), None, Some("no answer in 3 s"));
    assert!(!failed.reuse);
    assert_eq!(failed.misses, [ERROR]);
    assert_eq!(failed.detail, "the join's scan failed: no answer in 3 s");

    now.scope = "h:1001,1002|l:2001,2002".into();
    let scope = decide(Some(&saved), Some(&now), None);
    assert!(!scope.reuse);
    assert_eq!(scope.misses, [SCOPE]);
    assert_eq!(scope.detail, format!("scope {HOME_SCOPE} at the save, {} now", now.scope));

    let empty = fingerprint(&reply(NO_SCOPE, "0:cbf29ce484222325", "0:cbf29ce484222325", "0:cbf29ce484222325", "2.33"));
    let nothing = decide(Some(&empty), Some(&empty), None);
    assert!(!nothing.reuse);
    assert_eq!(nothing.misses, [SCOPE]);
    assert_eq!(nothing.detail, "nothing claimed yet, so nothing to compare");
}

#[test]
fn names_each_part_that_changed() {
    let saved = fingerprint(&default_reply());
    for (report, reasons) in [
        (default_reply().replace("54:6fba936a7d857f58", "55:0000000000000001"), vec![OBJECTS]),
        (default_reply().replace("28:230e39fcf3806d36", "28:0000000000000002"), vec![INVENTORIES]),
        (default_reply().replace("3:f90efb809f3ca050", "3:0000000000000003"), vec![RELATIONSHIPS]),
        (reply(HOME_SCOPE, "53:0000000000000004", "29:0000000000000005", "3:f90efb809f3ca050", "2.33"), vec![OBJECTS, INVENTORIES]),
    ] {
        let decision = decide(Some(&saved), Some(&fingerprint(&report)), None);
        assert!(!decision.reuse, "{report}");
        assert_eq!(decision.misses, reasons, "{report}");
    }
    let moved = fingerprint(&default_reply().replace("28:230e39fcf3806d36", "28:0000000000000002"));
    assert_eq!(decide(Some(&saved), Some(&moved), None).detail, "inventories 28 -> 28 (same count, other entries)");
    let changed = fingerprint(&reply(HOME_SCOPE, "53:01", "29:02", "4:03", "2.33"));
    let decision = decide(Some(&saved), Some(&changed), None);
    assert_eq!(decision.misses, [OBJECTS, INVENTORIES, RELATIONSHIPS]);
    assert_eq!(decision.detail, "objects 54 -> 53, inventories 28 -> 29, relationships 3 -> 4");
}

#[test]
fn counters_add_up_and_print_one_line() {
    let mut counters = JoinReuseCounters::new();
    assert_eq!(counters.format(), "hits=0 misses=0 (no-fingerprint-yet=0 scope=0 objects=0 inventories=0 relationships=0 error=0) scan-ms last=- max=- save-ms last=- max=- saves-skipped=0 seconds-saved=0.0");

    let saved = fingerprint(&default_reply());
    counters.record(&decide(None, None, None));
    counters.record_save(9876.0, true);
    counters.record_scan(3.3);
    counters.record(&decide(Some(&saved), Some(&saved), None));
    counters.record_scan(2.0);
    let changed = fingerprint(&reply(HOME_SCOPE, "53:01", "29:02", "3:f90efb809f3ca050", "2.33"));
    counters.record(&decide(Some(&saved), Some(&changed), None));
    counters.record_save(11539.0, true);
    counters.record_save(4000.0, false);
    counters.record_scan(2.5);
    counters.record(&decide(Some(&saved), Some(&saved), None));

    assert_eq!((counters.hits(), counters.saves_skipped(), counters.misses()), (2, 2, 2));
    assert_eq!(counters.misses_by(OBJECTS), 1);
    assert_eq!(counters.misses_by(INVENTORIES), 1);
    assert_eq!(counters.misses_by(NO_FINGERPRINT_YET), 1);
    assert_eq!(counters.misses_by("unknown"), 0);
    assert!((counters.seconds_saved() - 13.876).abs() < 1e-9);
    assert_eq!(counters.format(), "hits=2 misses=2 (no-fingerprint-yet=1 scope=0 objects=1 inventories=1 relationships=0 error=0) scan-ms last=2.5 max=3.3 save-ms last=11539 max=11539 saves-skipped=2 seconds-saved=13.9");
}

#[test]
fn parser_preserves_oracle_field_and_number_rules() {
    for report in [None, Some(""), Some(" \r\n "), Some(" none ")] {
        assert_eq!(WorldFingerprint::parse(report).unwrap_err(), "no fingerprint taken");
    }
    assert_eq!(WorldFingerprint::parse(Some("error=")).unwrap_err(), "");
    let valid = reply(HOME_SCOPE, "0:arbitrary:hash", "2147483647:b", "3:c", "bad");
    let parsed = fingerprint(&format!("  ignored;{valid};scope=last;ms= 1.25e1 \n"));
    assert_eq!(parsed.scope, "last");
    assert_eq!(parsed.objects.hash, "arbitrary:hash");
    assert_eq!(parsed.scan_ms, 12.5);
    for count in ["-1", "+1", " 1", "1 ", "2147483648", "1.0", ""] {
        assert!(WorldFingerprint::parse(Some(&reply(HOME_SCOPE, &format!("{count}:a"), "2:b", "3:c", "1"))).is_err());
    }
    for invalid in [
        reply("", "1:a", "2:b", "3:c", "1"),
        reply(HOME_SCOPE, "1:", "2:b", "3:c", "1"),
        default_reply().replace("objects=", "Objects="),
    ] {
        assert!(WorldFingerprint::parse(Some(&invalid)).is_err());
    }
    assert_eq!(fingerprint(&valid).scan_ms, 0.0);
    assert_eq!(fingerprint(&default_reply().replace(";ms=2.33", "")).scan_ms, 0.0);
    assert_eq!(fingerprint(&default_reply().replace(";ms=2.33", ";ms=inf")).scan_ms, 0.0);
    let long = "x".repeat(81);
    assert_eq!(WorldFingerprint::parse(Some(&long)).unwrap_err(), format!("not a fingerprint: {}...", "x".repeat(80)));
}

#[test]
fn absent_save_and_scan_errors_take_precedence_over_other_changes() {
    let saved = fingerprint(&default_reply());
    assert_eq!(decide(None, None, Some("injected failure")).misses, [NO_FINGERPRINT_YET]);
    assert_eq!(decide(Some(&saved), None, None).detail, "the join's scan failed: no reply");
    let failed = decide(Some(&saved), Some(&saved), Some("injected failure"));
    assert_eq!(failed.misses, [ERROR]);
    assert!(!failed.reuse);
    assert_eq!(failed.detail, "the join's scan failed: injected failure");
}

#[test]
fn counters_keep_maxima_and_use_public_saves_only_for_estimates() {
    let saved = fingerprint(&default_reply());
    let hit = decide(Some(&saved), Some(&saved), None);
    let mut counters = JoinReuseCounters::new();
    counters.record(&hit);
    assert_eq!(counters.seconds_saved(), 0.0);
    counters.record_save(9000.0, false);
    counters.record(&hit);
    assert!(counters.format().contains("save-ms last=- max=-"));
    assert_eq!(counters.seconds_saved(), 9.0);
    counters.record_save(100.0, true);
    counters.record_save(50.0, true);
    counters.record_scan(4.0);
    counters.record_scan(1.0);
    counters.record(&decide(Some(&saved), None, None));
    let mut changed = saved.clone();
    changed.scope = NO_SCOPE.into();
    counters.record(&decide(Some(&saved), Some(&changed), None));
    assert_eq!(counters.misses_by(ERROR), 1);
    assert_eq!(counters.misses_by(SCOPE), 1);
    assert!(counters.format().contains("scan-ms last=1.0 max=4.0 save-ms last=50 max=100"));
}

#[test]
#[cfg(feature = "repo-integration")]
fn join_lines_parse_with_the_actual_python_join_line_and_counters_patterns() {
    use std::io::Write;
    use std::process::{Command, Stdio};

    let saved = fingerprint(&default_reply());
    let mut changed = saved.clone();
    changed.objects.count += 1;
    changed.inventories.hash = "new".into();
    let mut counters = JoinReuseCounters::new();
    let miss = decide(Some(&saved), Some(&changed), None);
    let mut lines = vec![join_reuse_line(&decide(None, None, None), "no scan; saved fresh", &counters)];
    counters.record(&miss);
    counters.record_save(9876.0, true);
    counters.record_scan(2.5);
    let miss_line = join_reuse_line(&miss, "scan 2.5 ms in the game, 4 ms in all; saved fresh", &counters);
    assert_eq!(miss_line, format!("[world] join reuse: MISS (objects, inventories): objects 54 -> 55, inventories 28 -> 28 (same count, other entries); scan 2.5 ms in the game, 4 ms in all; saved fresh | {}", counters.format()));
    lines.push(miss_line);
    let hit = decide(Some(&saved), Some(&saved), None);
    counters.record(&hit);
    let hit_line = join_reuse_line(&hit, "scan 2.5 ms in the game, 4 ms in all; the last save served", &counters);
    assert_eq!(hit_line, format!("[world] join reuse: HIT: nothing a joiner needs changed since the last save; scan 2.5 ms in the game, 4 ms in all; the last save served | {}", counters.format()));
    lines.push(hit_line);

    // Evaluate only these two assignments, not the script's live-probe imports or entry point.
    let script = r#"
import ast, json, pathlib, re, sys
source = pathlib.Path(sys.argv[1])
tree = ast.parse(source.read_text(encoding="utf-8"), filename=str(source))
namespace = {"re": re}
for name in ("JOIN_LINE", "COUNTERS"):
    node, = [node for node in tree.body if isinstance(node, ast.Assign)
             and any(isinstance(target, ast.Name) and target.id == name for target in node.targets)]
    exec(compile(ast.Module(body=[node], type_ignores=[]), str(source), "exec"), namespace)
lines = json.load(sys.stdin)
for line in lines:
    found = namespace["JOIN_LINE"].search(line)
    assert found is not None, line
    counters = namespace["COUNTERS"].fullmatch(found.group(4))
    assert counters is not None, line
    assert len(counters.groups()) == 9
    for value in (counters.group(1), counters.group(2), counters.group(8)):
        int(value)
    for value in (counters.group(4), counters.group(5), counters.group(6), counters.group(7), counters.group(9)):
        assert value == "-" or float(value) >= 0
assert namespace["JOIN_LINE"].search(lines[1]).group(2) == "objects, inventories"
assert namespace["JOIN_LINE"].search(lines[2]).group(1) == "HIT"
print("JOIN_LINE and COUNTERS parsed all three Rust log lines")
"#;
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let mut python = Command::new("python")
        .current_dir(&root)
        .args(["-c", script])
        .arg(root.join("scripts/join_fingerprint.py"))
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("Python is required to check the existing log parser");
    python.stdin.take().unwrap().write_all(serde_json::to_string(&lines).unwrap().as_bytes()).unwrap();
    let output = python.wait_with_output().unwrap();
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
}
