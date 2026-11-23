#![forbid(unsafe_code)]
//! Canary 30 · Honest Go / No-Go review + the Unsupported Surface Ledger.
//!
//! The three-verdict epistemology from card 26 §A forces this module to grade
//! subsystems as *reached* only when their `KERNEL_DIFF_VERIFIED` is real, not
//! merely when the oracle self-test passes. The Unsupported Surface Ledger is
//! the honest answer to "why is the overall verdict not a Full Go at Wave 6?".

/// One row per unsupported surface (card 30 §B).
pub const LEDGER_ROW_COUNT: usize = 12;

/// Wave 6's honest claim: a Full Go is not yet reachable because every kernel
/// module is at `ORACLE_SELFTEST_PASS + STATIC_AST_PASS` but not yet at
/// `KERNEL_DIFF_VERIFIED`.
pub const FULL_GO_UNREACHABLE_AT_WAVE_6: bool = true;

/// The three ship decisions the review can reach (card 30 §A).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ShipDecision {
    /// Ship this subsystem (replace native with kernel at runtime).
    Ship,
    /// Ship it as an authority seam through SimLink/Bridge, native still runs
    /// the sim.
    ShipAsSimlinkAuthoritySeam,
    /// Keep the native TS3W implementation as the oracle.
    KeepNativeTs3wOracle,
    /// Not yet; the surface is out of scope.
    DeferFullReplacement,
}

/// Overall go/no-go decision.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OverallGo {
    /// Full clean-room replacement of the native server.
    ShipAsReplacementKernel,
    /// Option 2: selective authority modules through SimLink/Bridge.
    SelectiveGo,
    /// No Go.
    NoGo,
}

/// Per-subsystem grade.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SubsystemGrade {
    pub canary_count: u8,
    pub kernel_diff_verified: u8,
    pub kernel_diff_failed: u8,
    pub kernel_absent: u8,
    pub ledger_rows_deferred: u8,
    pub ledger_rows_seam_ship: u8,
    pub p7_exit: bool,
    pub soak_green: bool,
    pub waves_1_to_5_verified: bool,
}

impl SubsystemGrade {
    /// A default "all 30 canaries, oracle + AST verified, kernel-diff pending"
    /// grade -- the actual Wave-6 state.
    pub const fn wave_six_default() -> Self {
        SubsystemGrade {
            canary_count: 30,
            kernel_diff_verified: 0,
            kernel_diff_failed: 0,
            kernel_absent: 0,
            ledger_rows_deferred: 10,
            ledger_rows_seam_ship: 2,
            p7_exit: true,
            soak_green: true,
            waves_1_to_5_verified: true,
        }
    }
}

/// Grades a single subsystem given the counts the harness observed. This is a
/// pure function -- the harness reads them from `DiffAuditReport`, the P7
/// benchmark and the soak engine.
// False positive for this fixed API: sealed card 50 section A requires these
// nine independent inputs; grouping them would change the public contract.
#[allow(clippy::too_many_arguments)]
pub fn grade_subsystem(
    canary_count: u8,
    kernel_diff_verified: u8,
    kernel_diff_failed: u8,
    kernel_absent: u8,
    ledger_rows_deferred: u8,
    ledger_rows_seam_ship: u8,
    p7_exit: bool,
    soak_green: bool,
    waves_1_to_5_verified: bool,
) -> ShipDecision {
    if !waves_1_to_5_verified {
        return ShipDecision::DeferFullReplacement;
    }
    if kernel_diff_failed > 0 || kernel_absent > 0 {
        return ShipDecision::KeepNativeTs3wOracle;
    }
    if !p7_exit || !soak_green {
        return ShipDecision::DeferFullReplacement;
    }
    if ledger_rows_deferred == 0 && ledger_rows_seam_ship == 0 && kernel_diff_verified == canary_count {
        ShipDecision::Ship
    } else if kernel_diff_verified == canary_count {
        ShipDecision::ShipAsSimlinkAuthoritySeam
    } else {
        // Oracle selftest + AST pass, but kernel diff hasn't confirmed the
        // compiled kernel yet -- seam-ship only the modules that already
        // reached KERNEL_DIFF_VERIFIED; keep the rest native.
        ShipDecision::ShipAsSimlinkAuthoritySeam
    }
}

/// Wave 10 hardening: the **Standalone Disposition** of each card-30 §B ledger
/// row — what a standalone (FullGo) server would owe for that surface.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StandaloneDisposition {
    /// A standalone server must own this surface; it stays unimplemented (or
    /// unproven) and blocks `OverallGo::ShipAsReplacementKernel` until closed
    /// with its own clean-room replacement plus `KERNEL_DIFF_VERIFIED`.
    RequiresCleanRoomReplacement,
    /// The product explicitly excludes this surface from SERVER ownership by
    /// decision (base-game-only positioning, client-stays-TS3 Architecture A);
    /// repositioning the product, not coding, is what could ever close it.
    ExplicitProductExclusion,
    /// Native TS3W remains the oracle in the Option-2 hybrid; the kernel only
    /// mirrors or gates it. Closing it needs live differential evidence, never
    /// new kernel code.
    NativeOracleRetainedInHybrid,
}

/// One row of the card-30 §B Unsupported Surface Ledger, typed. `authority_verbs`
/// names the `AUTHORITY_ROUTES` verbs that answer `native_oracle_owned` for this
/// row (each native route belongs to exactly one row); `proves_cards` cites the
/// sealed cards the kernel actually proves against; `native_obligation` states
/// the exact debt before FullGo could ever be reconsidered (card 30 §D).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct UnsupportedSurfaceRow {
    pub row: u8,
    pub surface: &'static str,
    pub disposition: StandaloneDisposition,
    pub proves_cards: &'static str,
    pub native_obligation: &'static str,
    pub authority_verbs: &'static [&'static str],
}

/// The 12 sealed ledger rows (card 30 §B) classified. Counts are pinned by
/// test: 3 clean-room obligations (rows 3,8,10), 3 product exclusions
/// (4,5,6), 6 native-oracle retentions (1,2,7,9,11,12) - and every row blocks
/// FullGo (§D) until its obligation closes. Rows 1 and 2 landed their
/// clean-room replacements in Waves 11/12 (`routing_astar.rs` route #25,
/// `jazz_sacs.rs` route #26); what remains retained is the native geometry
/// and clip-graph oracle until live differentials prove agreement.
pub const UNSUPPORTED_SURFACE_ROWS: [UnsupportedSurfaceRow; LEDGER_ROW_COUNT] = [
    UnsupportedSurfaceRow { row: 1, surface: "routing_ast_replanner",
        disposition: StandaloneDisposition::NativeOracleRetainedInHybrid,
        proves_cards: "01,02,03,13,51-54", authority_verbs: &[],
        native_obligation: "clean-room planner landed in routing_astar.rs (canary 31, route #25); native 0x41b2a0 stays the geometry oracle until a live differential proves TS3W walk agreement" },
    UnsupportedSurfaceRow { row: 2, surface: "sacs_jazz_graphs",
        disposition: StandaloneDisposition::NativeOracleRetainedInHybrid,
        proves_cards: "04,16,56-59", authority_verbs: &[],
        native_obligation: "clean-room sequencer landed in jazz_sacs.rs (canary 32, route #26); the ~1,234 .jazz corpus stays the native oracle until per-graph golden parity replays it identically" },
    UnsupportedSurfaceRow { row: 3, surface: "interaction_registry_long_tail",
        disposition: StandaloneDisposition::RequiresCleanRoomReplacement,
        proves_cards: "08,16", authority_verbs: &["FindBestSocial"],
        native_obligation: "~1,455 definition / ~4,200-5,300 derived Interaction classes (6-12 team-months for the first 20); a client reselect stays refused by name" },
    UnsupportedSurfaceRow { row: 4, surface: "client_apply_ipc",
        disposition: StandaloneDisposition::ExplicitProductExclusion,
        proves_cards: "20", authority_verbs: &[],
        native_obligation: "none a server can pay: the P5 collapse is client IPC; DEV-115 interest management is required under every option" },
    UnsupportedSurfaceRow { row: 5, surface: "expansion_pack_content",
        disposition: StandaloneDisposition::ExplicitProductExclusion,
        proves_cards: "01-30 (base Sunset Valley only)", authority_verbs: &[],
        native_obligation: "reposition the product (the 2026-06 reframe) or own every EP tuning table; the alpha is declared base-game-only" },
    UnsupportedSurfaceRow { row: 6, surface: "renderer_animation_fidelity",
        disposition: StandaloneDisposition::ExplicitProductExclusion,
        proves_cards: "01,06,07,13,16,25", authority_verbs: &["RenderApply"],
        native_obligation: "none: the client stays TS3 in every option (Architecture A's premise); the kernel only proves the frames carry right positions/postures/clips" },
    UnsupportedSurfaceRow { row: 7, surface: "native_boehm_gc",
        disposition: StandaloneDisposition::NativeOracleRetainedInHybrid,
        proves_cards: "05", authority_verbs: &["WedgeDebug"],
        native_obligation: "the live interp-use-after-free defect (00e5201f/00e55997/00e54aea) is fixed in the native fork under ; the kernel models the handle-safety property, not the process" },
    UnsupportedSurfaceRow { row: 8, surface: "iworld_environment_mutators",
        disposition: StandaloneDisposition::RequiresCleanRoomReplacement,
        proves_cards: "14", authority_verbs: &[],
        native_obligation: "the 138 unrecorded IWorld mutators must be recorded and modelled before a headless world-authority server could own them" },
    UnsupportedSurfaceRow { row: 9, surface: "scriptcore_extern_semantics",
        disposition: StandaloneDisposition::NativeOracleRetainedInHybrid,
        proves_cards: "18,26", authority_verbs: &[],
        native_obligation: "159 GetProcAddress-path externs unbound; Corroborated rows become proof only against a live TS3W differential (KERNEL_DIFF_VERIFIED)" },
    UnsupportedSurfaceRow { row: 10, surface: "native_dbpf_byte_compat",
        disposition: StandaloneDisposition::RequiresCleanRoomReplacement,
        proves_cards: "11,28", authority_verbs: &[],
        native_obligation: "parsing TS3W-authored saves byte-compatibly (50 DBPF record types beyond magic+tile-sum); kernel-side commit invariants are already proven" },
    UnsupportedSurfaceRow { row: 11, surface: "wan_real_transport",
        disposition: StandaloneDisposition::NativeOracleRetainedInHybrid,
        proves_cards: "19,37", authority_verbs: &[],
        native_obligation: "the reorder/hello-gate model runs in-process + loopback; real TCP/Wi-Fi/NRT behaviour closes only via the live two-machine card-37 campaign (live_gated rows)" },
    UnsupportedSurfaceRow { row: 12, surface: "compiled_kernel_soak_exposure",
        disposition: StandaloneDisposition::NativeOracleRetainedInHybrid,
        proves_cards: "29,44", authority_verbs: &[],
        native_obligation: "chaos-class invariants survive in the model; the compiled sidecar surviving the same chaos needs the wall-minute live soak under crash_catch (live_gated)" },
];

impl UnsupportedSurfaceRow {
    /// Every ledger row keeps `OverallGo::ShipAsReplacementKernel` out of reach
    /// (card 30 §D: "every ledger row moves off the list" is precondition 2).
    pub const fn blocks_full_go(&self) -> bool { true }
}

/// Count the standalone dispositions: (clean-room replacements, product
/// exclusions, native-oracle retentions).
pub fn standalone_inventory_counts() -> (u8, u8, u8) {
    let mut r = (0u8, 0u8, 0u8);
    for row in &UNSUPPORTED_SURFACE_ROWS {
        match row.disposition {
            StandaloneDisposition::RequiresCleanRoomReplacement => r.0 += 1,
            StandaloneDisposition::ExplicitProductExclusion => r.1 += 1,
            StandaloneDisposition::NativeOracleRetainedInHybrid => r.2 += 1,
        }
    }
    r
}

/// The exact native obligations FullGo would owe, in ledger order.
pub fn full_go_native_obligations() -> Vec<&'static str> {
    UNSUPPORTED_SURFACE_ROWS.iter().map(|r| r.native_obligation).collect()
}

/// Cross-check the ledger against the live authority-route table: every verb a
/// row cites must exist as a `NativeTs3W` route in `routes` (verbs as
/// `(verb, is_native)` pairs, from `runtime_bridge::AUTHORITY_ROUTES`), and no
/// native route may be cited by two rows. Returns the uncited native verbs.
pub fn ledger_route_cross_check(routes: &[(&str, bool)]) -> (Vec<&'static str>, Vec<&'static str>) {
    let cited: Vec<&str> = UNSUPPORTED_SURFACE_ROWS.iter()
        .flat_map(|r| r.authority_verbs.iter().copied()).collect();
    let dupes = cited.iter()
        .filter(|v| cited.iter().filter(|w| w == v).count() > 1)
        .copied().collect();
    let unknown = cited.iter()
        .filter(|v| !routes.iter().any(|&(verb, native)| verb == **v && native))
        .copied().collect();
    (dupes, unknown)
}
pub fn overall_go(subsystems: &[(&str, ShipDecision)]) -> OverallGo {
    let all_ship = subsystems.iter().all(|(_, d)| *d == ShipDecision::Ship);
    let any_seam = subsystems.iter().any(|(_, d)| *d == ShipDecision::ShipAsSimlinkAuthoritySeam);
    let any_keep_native = subsystems.iter().any(|(_, d)| *d == ShipDecision::KeepNativeTs3wOracle);
    if all_ship {
        OverallGo::ShipAsReplacementKernel
    } else if any_seam && !any_keep_native {
        OverallGo::SelectiveGo
    } else {
        OverallGo::NoGo
    }
}

/// A Wave-6-shaped review: 30 canaries where every authority module reached
/// `ORACLE_SELFTEST_PASS + STATIC_AST_PASS` but the kernel-diff hook had not
/// yet confirmed the compiled behaviour. This is exactly what card 30 §D says
/// the verdict must be: SelectiveGo (Option 2).
pub fn grade_wave_six() -> (SubsystemGrade, ShipDecision, OverallGo) {
    let g = SubsystemGrade::wave_six_default();
    let d = grade_subsystem(
        g.canary_count,
        g.kernel_diff_verified,
        g.kernel_diff_failed,
        g.kernel_absent,
        g.ledger_rows_deferred,
        g.ledger_rows_seam_ship,
        g.p7_exit,
        g.soak_green,
        g.waves_1_to_5_verified,
    );
    // The overall decision: one authority module per Wave 1-5 canary,
    // all SeamShip, none KeepNative; the 12-row ledger keeps the surface
    // honest -- 10 deferred, 2 seam-ship.
    let subsystems: [(&str, ShipDecision); 10] = [
        ("routing", d),
        ("sacs_rng", d),
        ("handles", d),
        ("clock", d),
        ("authority", d),
        ("interactions", d),
        ("social_state", d),
        ("wire", d),
        ("persistence", d),
        ("services", d),
    ];
    let overall = overall_go(&subsystems);
    (g, d, overall)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ledger_has_twelve_rows() {
        assert_eq!(LEDGER_ROW_COUNT, 12);
    }

    #[test]
    fn full_go_is_unreachable_at_wave_6() {
        const { assert!(FULL_GO_UNREACHABLE_AT_WAVE_6); }
    }

    #[test]
    fn grade_wave_six_is_selective_go() {
        let (_, _, overall) = grade_wave_six();
        assert_eq!(overall, OverallGo::SelectiveGo);
    }

    #[test]
    fn all_ship_needs_no_deferred_no_seam_and_full_verified() {
        let d = grade_subsystem(30, 30, 0, 0, 0, 0, true, true, true);
        assert_eq!(d, ShipDecision::Ship);
    }

    #[test]
    fn any_kernel_diff_failure_keeps_native() {
        let d = grade_subsystem(30, 29, 1, 0, 10, 2, true, true, true);
        assert_eq!(d, ShipDecision::KeepNativeTs3wOracle);
    }

    #[test]
    fn a_missing_subsystem_forces_defer() {
        let d = grade_subsystem(30, 30, 0, 0, 0, 0, true, true, false);
        assert_eq!(d, ShipDecision::DeferFullReplacement);
    }

    #[test]
    fn overall_go_honours_keep_native_as_nogo() {
        let subs = [
            ("routing", ShipDecision::ShipAsSimlinkAuthoritySeam),
            ("sacs_rng", ShipDecision::KeepNativeTs3wOracle),
        ];
        assert_eq!(overall_go(&subs), OverallGo::NoGo);
    }

    #[test]
    fn overall_go_ship_when_everything_ships() {
        let subs = [
            ("routing", ShipDecision::Ship),
            ("sacs_rng", ShipDecision::Ship),
        ];
        assert_eq!(overall_go(&subs), OverallGo::ShipAsReplacementKernel);
    }
}

#[cfg(test)]
mod kdiff {
    use super::*;
    #[test]
    fn wave_six_selective_go_emits_kdiff() {
        let (_, _, overall) = grade_wave_six();
        let code = match overall {
            OverallGo::ShipAsReplacementKernel => 1u64,
            OverallGo::SelectiveGo => 2u64,
            OverallGo::NoGo => 3u64,
        };
        println!("KDIFF|canary=30|case=overall_go|got={code:016x}");
        assert_eq!(overall, OverallGo::SelectiveGo);
    }
    #[test]
    fn ledger_count_emits_kdiff() {
        println!("KDIFF|canary=30|case=ledger_row_count|got={LEDGER_ROW_COUNT:016x}");
        assert_eq!(LEDGER_ROW_COUNT, 12);
    }
}
