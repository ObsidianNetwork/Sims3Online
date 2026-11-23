//! Chooses the kernel's oracle build (review F5, fix 1).
//!
//! `sim96_private_oracles` is set when the `private-oracles` feature is on, or
//! when both git-ignored oracles (`src/routing.rs`, `src/sacs_rng.rs`) exist.
//! So a checkout that has the oracles builds exactly as before F5 with a plain
//! `cargo test` (the sealed referee passes no `--features`), and a fresh clone
//! builds the tracked interface. `SIM96_FORCE_DEFAULT_KERNEL=1` builds the
//! interface even when the files exist (the local feature-off gate,
//! `docs/dev-tools.md`); an explicit `--features private-oracles` wins over it.

use std::path::Path;

const ORACLES: [&str; 2] = ["src/routing.rs", "src/sacs_rng.rs"];

fn main() {
    println!("cargo:rustc-check-cfg=cfg(sim96_private_oracles)");
    println!("cargo:rerun-if-env-changed=SIM96_FORCE_DEFAULT_KERNEL");
    // The directory too: restoring a moved file keeps its old mtime.
    println!("cargo:rerun-if-changed=src");
    for file in ORACLES {
        println!("cargo:rerun-if-changed={file}");
    }

    let feature = std::env::var_os("CARGO_FEATURE_PRIVATE_ORACLES").is_some();
    let forced_off = std::env::var("SIM96_FORCE_DEFAULT_KERNEL").is_ok_and(|v| v == "1");
    let present = ORACLES.iter().all(|file| Path::new(file).is_file());
    if feature || (present && !forced_off) {
        println!("cargo:rustc-cfg=sim96_private_oracles");
    }
}
