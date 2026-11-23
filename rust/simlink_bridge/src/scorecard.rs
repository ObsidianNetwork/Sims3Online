//! The §H scorecard line and the §D U1 unsafe count.
//!
//! `verbs_declared` is re-read from `BridgeCommand.cs` at run time; `verbs_refused` is the §E.3 count;
//! `verbs_roundtripped` counts verbs with both an encoder diff (gate 4) and a reply-shape diff (gate 5),
//! plus the refusals; `verbs_live` counts verbs round-tripped against the live mod (gate 6), plus the
//! refusals; `unsafe_modules` counts source files other than `src/ffi.rs` that contain the `unsafe` keyword.
#![forbid(unsafe_code)]

use std::path::{Path, PathBuf};

/// The machine-readable scorecard.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Scorecard {
    pub verbs_declared: usize,
    pub verbs_roundtripped: usize,
    pub verbs_refused: usize,
    pub verbs_live: usize,
    pub unsafe_modules: usize,
}

impl Scorecard {
    pub fn line(&self) -> String {
        format!(
            "verbs_declared={}|verbs_roundtripped={}|verbs_refused={}|verbs_live={}|unsafe_modules={}",
            self.verbs_declared, self.verbs_roundtripped, self.verbs_refused, self.verbs_live, self.unsafe_modules
        )
    }

    /// The normative invariant: everything round-tripped, everything live, no unsafe outside `ffi.rs`.
    pub fn holds(&self) -> bool {
        self.verbs_roundtripped == self.verbs_declared
            && self.verbs_live == self.verbs_declared
            && self.unsafe_modules == 0
    }

    /// The offline part of the invariant (gates 4, 5 and 7), which this card can meet without the game.
    pub fn offline_holds(&self) -> bool {
        self.verbs_roundtripped == self.verbs_declared && self.unsafe_modules == 0
    }
}

/// Occurrences of the `unsafe` keyword as a token in Rust source (card 56 §H `unsafe_modules`): comments
/// (`//`, nested `/* */`), string, byte-string, raw-string, char and byte literals are removed first, and
/// `unsafe_code`, `unsafe_op_in_unsafe_fn` and the raw identifier `r#unsafe` are other identifiers.
pub fn count_unsafe_keyword(source: &str) -> usize {
    words(&code_only(source)).filter(|w| *w == "unsafe").count()
}

fn is_word_char(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '_'
}

/// The `[A-Za-z0-9_]` runs of `code`, in order.
fn words(code: &str) -> impl Iterator<Item = &str> {
    code.split(|c: char| !is_word_char(c)).filter(|w| !w.is_empty())
}

/// The source with every comment and literal replaced by one space (newlines inside them kept, so line
/// numbers survive). A lifetime (`'a`) stays code; a char literal (`'x'`, `'\''`, `b'"'`) does not.
pub fn code_only(source: &str) -> String {
    let s: Vec<char> = source.chars().collect();
    let n = s.len();
    let at = |i: usize| s.get(i).copied();
    let mut out = String::with_capacity(source.len());
    let mut i = 0;
    // Skip `s[from..]` up to and including the `'` or `"` that closes a literal with backslash escapes.
    let skip_escaped = |mut i: usize, close: char, out: &mut String| -> usize {
        while i < n {
            match s[i] {
                '\\' => i += 2,
                c if c == close => return i + 1,
                '\n' => {
                    out.push('\n');
                    i += 1;
                }
                _ => i += 1,
            }
        }
        n
    };
    while i < n {
        let c = s[i];
        if c == '/' && at(i + 1) == Some('/') {
            while i < n && s[i] != '\n' {
                i += 1;
            }
        } else if c == '/' && at(i + 1) == Some('*') {
            let mut depth = 1;
            i += 2;
            while i < n && depth > 0 {
                if s[i] == '/' && at(i + 1) == Some('*') {
                    depth += 1;
                    i += 2;
                } else if s[i] == '*' && at(i + 1) == Some('/') {
                    depth -= 1;
                    i += 2;
                } else {
                    if s[i] == '\n' {
                        out.push('\n');
                    }
                    i += 1;
                }
            }
            out.push(' ');
        } else if c == '"' {
            i = skip_escaped(i + 1, '"', &mut out);
            out.push(' ');
        } else if c == '\'' {
            if at(i + 1) == Some('\\') || (at(i + 2) == Some('\'') && at(i + 1) != Some('\'')) {
                i = skip_escaped(i + 1, '\'', &mut out);
                out.push(' ');
            } else {
                out.push(c);
                i += 1;
            }
        } else if is_word_char(c) {
            let start = i;
            while i < n && is_word_char(s[i]) {
                i += 1;
            }
            let word: String = s[start..i].iter().collect();
            let raw_prefix = matches!(word.as_str(), "r" | "br" | "cr");
            let mut hashes = 0;
            while raw_prefix && at(i + hashes) == Some('#') {
                hashes += 1;
            }
            if raw_prefix && at(i + hashes) == Some('"') {
                // A raw string: no escapes; it ends at `"` followed by the same number of `#`.
                i += hashes + 1;
                while i < n && !(s[i] == '"' && (1..=hashes).all(|k| at(i + k) == Some('#'))) {
                    if s[i] == '\n' {
                        out.push('\n');
                    }
                    i += 1;
                }
                i = (i + 1 + hashes).min(n);
                out.push(' ');
            } else if word == "r" && hashes == 1 && at(i + 1).is_some_and(is_word_char) {
                // A raw identifier (`r#unsafe`): an identifier, never the keyword.
                i += 1;
                while i < n && is_word_char(s[i]) {
                    i += 1;
                }
                out.push(' ');
            } else if matches!(word.as_str(), "b" | "c") && at(i) == Some('"') {
                i = skip_escaped(i + 1, '"', &mut out);
                out.push(' ');
            } else if word == "b" && at(i) == Some('\'') {
                i = skip_escaped(i + 1, '\'', &mut out);
                out.push(' ');
            } else {
                out.push_str(&word);
            }
        } else {
            out.push(c);
            i += 1;
        }
    }
    out
}

/// Every `.rs` file under `dir` (recursively, skipping `target*` directories), sorted.
pub fn rust_files(dir: &Path) -> std::io::Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        for entry in std::fs::read_dir(&d)? {
            let path = entry?.path();
            let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
            if path.is_dir() {
                if !name.starts_with("target") && name != "oracle" {
                    stack.push(path);
                }
            } else if name.ends_with(".rs") {
                out.push(path);
            }
        }
    }
    out.sort();
    Ok(out)
}

/// `(path relative to the crate, unsafe count, carries #![forbid(unsafe_code)])` for every Rust file.
pub fn audit(crate_dir: &Path) -> std::io::Result<Vec<(String, usize, bool)>> {
    let mut rows = Vec::new();
    for path in rust_files(crate_dir)? {
        let source = std::fs::read_to_string(&path)?;
        let rel = path.strip_prefix(crate_dir).unwrap_or(&path).to_string_lossy().replace('\\', "/");
        rows.push((rel, count_unsafe_keyword(&source), source.contains("#![forbid(unsafe_code)]")));
    }
    Ok(rows)
}

/// The lint level an attribute body sets for `unsafe_code` when it lifts the lint: `allow`, `expect` or `warn`, or
/// `cfg_attr` when one of those sits under a `cfg_attr`. `deny` and `forbid` do not lift it and give `None`.
fn lifting_attribute(body: &str) -> Option<&'static str> {
    let names: Vec<&str> = words(body).collect();
    if !names.contains(&"unsafe_code") {
        return None;
    }
    let lifts = ["allow", "expect", "warn"];
    match names.first().copied() {
        Some("allow") => Some("allow"),
        Some("expect") => Some("expect"),
        Some("warn") => Some("warn"),
        Some("cfg_attr") if names.iter().any(|w| lifts.contains(w)) => Some("cfg_attr"),
        _ => None,
    }
}

/// Every attribute that lifts the `unsafe_code` lint in `source` (comments and literals removed), as
/// `(attribute, target)`: the attribute is `allow`, `expect`, `warn` or `cfg_attr` ([`lifting_attribute`]); the
/// target is `"inner"` for `#![..]`, else the head of the item it applies to (`pub mod ffi`), whitespace collapsed.
pub fn allow_unsafe_sites(source: &str) -> Vec<(&'static str, String)> {
    let code: Vec<char> = code_only(source).chars().collect();
    let n = code.len();
    let skip_ws = |mut i: usize| {
        while i < n && code[i].is_whitespace() {
            i += 1;
        }
        i
    };
    // `i` at `#`: `(inner, body, index after the closing `]`)` of the attribute there, if it is one.
    let attribute = |i: usize| -> Option<(bool, String, usize)> {
        let mut j = skip_ws(i + 1);
        let inner = code.get(j) == Some(&'!');
        if inner {
            j = skip_ws(j + 1);
        }
        if code.get(j) != Some(&'[') {
            return None;
        }
        let (start, mut depth) = (j + 1, 0usize);
        while j < n {
            match code[j] {
                '[' => depth += 1,
                ']' => {
                    depth -= 1;
                    if depth == 0 {
                        return Some((inner, code[start..j].iter().collect(), j + 1));
                    }
                }
                _ => {}
            }
            j += 1;
        }
        None
    };
    let mut sites = Vec::new();
    let mut i = 0;
    while i < n {
        if code[i] != '#' {
            i += 1;
            continue;
        }
        let Some((inner, body, after)) = attribute(i) else {
            i += 1;
            continue;
        };
        if let Some(attr) = lifting_attribute(body.trim()) {
            if inner {
                sites.push((attr, "inner".to_string()));
            } else {
                // The item: skip any further attributes, then read up to `;` or `{`.
                let mut k = skip_ws(after);
                while code.get(k) == Some(&'#') {
                    match attribute(k) {
                        Some((_, _, next)) => k = skip_ws(next),
                        None => break,
                    }
                }
                let head: String = code[k..].iter().take_while(|c| **c != ';' && **c != '{').collect();
                sites.push((attr, head.split_whitespace().collect::<Vec<_>>().join(" ")));
            }
        }
        i = after;
    }
    sites
}

/// Card 56 §D U1: exactly one attribute in the crate lifts `unsafe_code`, and it is the outer `#[allow(unsafe_code)]`
/// on `mod ffi` in the crate root `src/lib.rs`. `expect`, `warn` and a `cfg_attr` wrapping any of them also lift
/// the lint, so they count as sites too. `files` is `(path relative to the crate, source)`. `Ok` names the one
/// site; `Err` lists every site found (none, two or more, or one in another form or place).
pub fn check_allow_unsafe(files: &[(String, String)]) -> Result<String, String> {
    let sites: Vec<(String, &str, String)> = files
        .iter()
        .flat_map(|(path, source)| allow_unsafe_sites(source).into_iter().map(move |(a, t)| (path.clone(), a, t)))
        .collect();
    let shown = |(path, attr, target): &(String, &str, String)| {
        if *attr == "allow" { format!("{path}: {target}") } else { format!("{path}: #[{attr}] {target}") }
    };
    let on_ffi = |(path, attr, target): &(String, &str, String)| {
        path == "src/lib.rs" && *attr == "allow" && target.split_whitespace().rev().take(2).eq(["ffi", "mod"])
    };
    let sites_shown: Vec<String> = sites.iter().map(shown).collect();
    match sites.as_slice() {
        [one] if on_ffi(one) => Ok(shown(one)),
        _ => Err(format!(
            "allow(unsafe_code) must occur exactly once, on mod ffi in src/lib.rs (card 56 U1); found {}: [{}]",
            sites.len(),
            sites_shown.join("; ")
        )),
    }
}

/// [`check_allow_unsafe`] over every Rust file of the crate at `crate_dir`.
pub fn allow_unsafe_check(crate_dir: &Path) -> std::io::Result<Result<String, String>> {
    let mut files = Vec::new();
    for path in rust_files(crate_dir)? {
        let source = std::fs::read_to_string(&path)?;
        let rel = path.strip_prefix(crate_dir).unwrap_or(&path).to_string_lossy().replace('\\', "/");
        files.push((rel, source));
    }
    Ok(check_allow_unsafe(&files))
}

/// Files other than `src/ffi.rs` with a non-zero unsafe count.
pub fn unsafe_modules(audit_rows: &[(String, usize, bool)]) -> usize {
    audit_rows.iter().filter(|(path, n, _)| path != "src/ffi.rs" && *n > 0).count()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn counts_the_keyword_not_the_lint_name() {
        assert_eq!(count_unsafe_keyword("#![forbid(unsafe_code)]\nfn a() {}"), 0);
        assert_eq!(count_unsafe_keyword("// an unsafe comment\nlet s = \"unsafe\";"), 0);
        assert_eq!(count_unsafe_keyword("unsafe { x() }\nunsafe impl Send for T {}"), 2);
        assert_eq!(count_unsafe_keyword("#![deny(unsafe_op_in_unsafe_fn)]"), 0);
    }

    #[test]
    fn block_comments_are_not_code_nested_or_across_lines() {
        assert_eq!(count_unsafe_keyword("/* unsafe */ fn a() {}"), 0);
        assert_eq!(count_unsafe_keyword("/* outer /* unsafe */ still unsafe */ fn a() {}"), 0);
        assert_eq!(count_unsafe_keyword("/*\n unsafe {\n*/\nfn a() {}"), 0);
        assert_eq!(count_unsafe_keyword("/** doc unsafe */ unsafe fn a() {}"), 1);
    }

    #[test]
    fn char_literals_do_not_open_a_string_and_lifetimes_are_code() {
        assert_eq!(count_unsafe_keyword("let q = '\"'; unsafe { x() }"), 1);
        assert_eq!(count_unsafe_keyword("let q = '\\''; unsafe { x() }"), 1);
        assert_eq!(count_unsafe_keyword("let q = b'\"'; unsafe { x() }"), 1);
        assert_eq!(count_unsafe_keyword("fn f<'a>(x: &'a str) { unsafe { g(x) } }"), 1);
    }

    #[test]
    fn raw_and_multi_line_strings_are_not_code() {
        assert_eq!(count_unsafe_keyword("let s = r#\"he said \"unsafe\" ok\"#;"), 0);
        assert_eq!(count_unsafe_keyword("let s = r\"unsafe\"; let t = br##\"a\"# unsafe\"##;"), 0);
        assert_eq!(count_unsafe_keyword("let s = \"first\nunsafe {\n\";\nfn a() {}"), 0);
        assert_eq!(count_unsafe_keyword("let s = \"a\\\"\"; unsafe { x() }"), 1);
        assert_eq!(count_unsafe_keyword("let r#unsafe = 1;"), 0);
    }

    fn files(list: &[(&str, &str)]) -> Vec<(String, String)> {
        list.iter().map(|(p, s)| (p.to_string(), s.to_string())).collect()
    }

    const LIB_OK: &str = "#![deny(unsafe_code)]\npub mod a;\n#[allow(unsafe_code)]\npub mod ffi;\n";
    const QUIET: &str = "#![forbid(unsafe_code)]\n// #[allow(unsafe_code)] in a comment\nconst S: &str = \"#[allow(unsafe_code)]\";\n";

    #[test]
    fn allow_unsafe_code_once_on_mod_ffi_passes() {
        assert_eq!(check_allow_unsafe(&files(&[("src/lib.rs", LIB_OK), ("src/x.rs", QUIET)])), Ok("src/lib.rs: pub mod ffi".to_string()));
        // A `deny` or `forbid` of the lint, plain or under cfg_attr, does not lift it and is not a site.
        let deny = "#![forbid(unsafe_code)]\n#[cfg_attr(test, deny(unsafe_code))]\nfn f() {}\n";
        assert!(check_allow_unsafe(&files(&[("src/lib.rs", LIB_OK), ("src/x.rs", deny)])).is_ok());
    }

    #[test]
    fn the_one_allow_is_the_outer_attribute_on_mod_ffi_in_lib_rs() {
        // Card 56 U1 (23e5ff55): the crate root carries the one allow; the inner form in ffi.rs is not it.
        let inner = files(&[("src/lib.rs", "#![deny(unsafe_code)]\npub mod ffi;\n"), ("src/ffi.rs", "#![allow(unsafe_code)]\n")]);
        assert!(check_allow_unsafe(&inner).is_err());
        let elsewhere = files(&[("src/other.rs", "#[allow(unsafe_code)]\npub mod ffi;\n")]);
        assert!(check_allow_unsafe(&elsewhere).is_err());
    }

    #[test]
    fn expect_warn_and_cfg_attr_allow_also_lift_the_lint_and_count() {
        for extra in [
            "#[expect(unsafe_code)]\nfn f() {}\n",
            "#[warn(unsafe_code)]\nfn f() {}\n",
            "#[cfg_attr(all(), allow(unsafe_code))]\nfn f() {}\n",
            "#![cfg_attr(windows, expect(dead_code, unsafe_code))]\n",
        ] {
            assert!(check_allow_unsafe(&files(&[("src/lib.rs", LIB_OK), ("src/x.rs", extra)])).is_err(), "{extra}");
        }
        // Only the plain `allow` is the ruled form, even on mod ffi in lib.rs.
        for lib in ["#![deny(unsafe_code)]\n#[expect(unsafe_code)]\npub mod ffi;\n", "#![deny(unsafe_code)]\n#[cfg_attr(all(), allow(unsafe_code))]\npub mod ffi;\n"] {
            assert!(check_allow_unsafe(&files(&[("src/lib.rs", lib)])).is_err(), "{lib}");
        }
    }

    #[test]
    fn allow_unsafe_code_zero_or_twice_or_elsewhere_fails() {
        assert!(check_allow_unsafe(&files(&[("src/lib.rs", "#![deny(unsafe_code)]\npub mod ffi;\n"), ("src/x.rs", QUIET)])).is_err());
        assert!(check_allow_unsafe(&files(&[("src/lib.rs", LIB_OK), ("src/x.rs", "#[allow(unsafe_code)]\nfn f() {}\n")])).is_err());
        assert!(check_allow_unsafe(&files(&[("src/lib.rs", LIB_OK), ("src/x.rs", "#[allow(dead_code, unsafe_code)]\nfn f() {}\n")])).is_err());
        assert!(check_allow_unsafe(&files(&[("src/lib.rs", "#[allow(unsafe_code)]\npub mod memory;\npub mod ffi;\n")])).is_err());
        assert!(check_allow_unsafe(&files(&[("src/x.rs", "#![allow(unsafe_code)]\n")])).is_err());
    }

    #[test]
    fn the_crate_allows_unsafe_code_once_on_mod_ffi() {
        let dir = Path::new(env!("CARGO_MANIFEST_DIR"));
        assert_eq!(allow_unsafe_check(dir).unwrap(), Ok("src/lib.rs: pub mod ffi".to_string()));
    }

    #[test]
    fn the_crate_has_unsafe_only_in_ffi_and_forbids_it_elsewhere() {
        let dir = Path::new(env!("CARGO_MANIFEST_DIR"));
        let rows = audit(dir).unwrap();
        assert!(rows.iter().any(|(p, n, _)| p == "src/ffi.rs" && *n > 0));
        assert_eq!(unsafe_modules(&rows), 0, "{rows:?}");
        for (path, _, forbids) in &rows {
            if path != "src/ffi.rs" && path != "src/lib.rs" {
                assert!(forbids, "{path} does not carry #![forbid(unsafe_code)]");
            }
        }
    }

    #[test]
    fn scorecard_line_and_invariant() {
        let s = Scorecard { verbs_declared: 60, verbs_roundtripped: 60, verbs_refused: 3, verbs_live: 3, unsafe_modules: 0 };
        assert_eq!(s.line(), "verbs_declared=60|verbs_roundtripped=60|verbs_refused=3|verbs_live=3|unsafe_modules=0");
        assert!(s.offline_holds());
        assert!(!s.holds());
    }
}
