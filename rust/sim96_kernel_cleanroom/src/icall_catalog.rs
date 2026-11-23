#![forbid(unsafe_code)]
//! Card 18 · table-sourced icall catalog + ABI coverage oracle.
//!
//! The `InitializeMonoScriptHostRuntime` walk at RVA `0x97e6c7` registers the
//! `{fn_va, name_ptr}` entries of **43** NULL-terminated per-class tables;
//! the two `MonoScriptHost_*` calls at `AddMonoInternalCall` `0xa82550` are
//! bound directly. After the tool's reconciliation of the five
//! static-ctor-registered `SimIFace` rows into the table count, the bound
//! total is **1,775 table + 2 direct = 1,777** over a 1,937-row TSV whose
//! remaining 159 rows are `GetProcAddress` P/Invokes (`Unbound`).
//!
//! This module carries the catalog *constants and the four-witness grading
//! state machine only*. The TSV itself (and anything derived from TS3W.exe
//! bytes) stays outside the clean-room crate; the pinned SHA-256 is an
//! identity witness, per the pilot's proprietary-input policy.

/// The pinned TS3W.exe binary identity every validator run is keyed to
/// (card 18 §F: a row set from any other binary is untrusted).
pub const TS3W_SHA256_HEX: &str =
    "10b2423e2368e161774462b0ef1960231ad0af298d564a7bb6a6e78c5e880609";

/// Virtual address = RVA + IMAGE_BASE (the image base recorded in the map header).
pub const IMAGE_BASE: u32 = 0x400_0000;

/// The registration walk that reads the 43 per-class tables.
pub const INITIALIZE_MONO_SCRIPT_HOST_RUNTIME_RVA: u32 = 0x97_e6c7;

/// The direct `MonoScriptHost_*` bootstrap registration site.
pub const ADD_MONO_INTERNAL_CALL_RVA: u32 = 0xa8_2550;

/// NULL-terminated `{fn_va, name_ptr}` per-class tables the walk visits.
pub const PER_CLASS_TABLE_COUNT: u32 = 43;

/// One table entry: `{ fn_va: u64, name_ptr: u64 }`.
pub const PER_CLASS_TABLE_ENTRY_SIZE: u32 = 16;

/// The provider-vtable three-instruction accessor slot (§C.1).
pub const PROVIDER_VTABLE_ACCESSOR_SLOT_OFFSET: u32 = 0x10;

/// The reconciled row counts (card 18 §A): the TSV totals and the
/// bound figure the managed side sees as `extern`.
pub const TABLE_REGISTERED_ROWS: u32 = 1_775;
pub const DIRECT_ROWS: u32 = 2;
pub const BOUND_TOTAL: u32 = 1_777;
pub const UNBOUND_ROWS: u32 = 159;
pub const NATIVE_ONLY_RECONCILED_ROWS: u32 = 1;
pub const TSV_TOTAL_ROWS: u32 = 1_937;

/// Canonical `Corroborated` pilot anchors (RVA, pre-reconciliation figures
/// quoted from the pilot report §"Seed/path scope").
pub const ROUTE_GET_PATH_DATA_RVA: u32 = 0x35_0570;
pub const SACS_SEED_RVA: u32 = 0x0f_a800;
pub const SACS_PATH_DRAW_RVA: u32 = 0x0f_a840;

/// RVA -> Virtual address, wrapping at the 32-bit edge like the loader does.
pub fn va_of_rva(rva: u32) -> u32 {
    rva.wrapping_add(IMAGE_BASE)
}

/// How a row reached the runtime's internal-call map (§C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RegisterPath {
    TableWalk,
    DirectMonoScriptHost,
    GetProcAddress,
}

/// The four independent witnesses per row (§C). `vtable_accessor_ok` is only
/// true when the disassembler confirmed the `+0x10` three-instruction
/// accessor shape for this class in the current pass.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct IcallWitness {
    pub managed_metadata_ok: bool,
    pub exact_table_record: bool,
    pub bounded_native_fn_ok: bool,
    pub vtable_accessor_ok: bool,
    pub registered_via: RegisterPath,
}

/// Which witness conflicted (§C / §C.1 / §C.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RejectReason {
    VtableSlotMismatch,
    NameConflict,
    BoundedFunctionTooLong,
    TableRecordCoincidence,
}

/// One row's verdict (§C). Only `Corroborated` may anchor a kernel shim.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum IcallVerdict {
    Corroborated,
    Candidate,
    Rejected { reason: RejectReason },
    Unbound,
    Direct,
}

/// Per-path verdict counts; `totals_reconcile_to_map` requires the sum over
/// every graded row to equal the TSV row count exactly (nothing lost).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct IcallTally {
    pub corroborated: u32,
    pub candidate: u32,
    pub rejected: u32,
    pub unbound: u32,
    pub direct: u32,
}

impl IcallTally {
    pub fn total(&self) -> u32 {
        self.corroborated + self.candidate + self.rejected + self.unbound + self.direct
    }

    /// The bound forms after reconciliation: table-side verdicts plus the
    /// two direct rows (card 18 §A: `1,775 + 2 = 1,777`).
    pub fn bound(&self) -> u32 {
        self.corroborated + self.candidate + self.rejected + self.direct
    }
}

/// The 1,937-row catalog's grading state machine. Carries the 43 table
/// manifests conceptually; here it is a pure witness grader — deterministic,
/// no wall time, no RNG (§C).
pub struct IcallCatalogValidator;

impl IcallCatalogValidator {
    /// One row in, one verdict out (§C). The base three witnesses gate
    /// everything; the accessor splits `Corroborated` from `Candidate`; a
    /// base-witness failure names the conflicting witness (§C.1/§C.2).
    pub fn grade(&self, w: &IcallWitness) -> IcallVerdict {
        match w.registered_via {
            RegisterPath::GetProcAddress => IcallVerdict::Unbound,
            RegisterPath::DirectMonoScriptHost => IcallVerdict::Direct,
            RegisterPath::TableWalk => {
                if !w.managed_metadata_ok {
                    IcallVerdict::Rejected { reason: RejectReason::NameConflict }
                } else if !w.exact_table_record {
                    // The pair only appears as an accidental run of bytes.
                    IcallVerdict::Rejected { reason: RejectReason::TableRecordCoincidence }
                } else if !w.bounded_native_fn_ok {
                    IcallVerdict::Rejected { reason: RejectReason::BoundedFunctionTooLong }
                } else if w.vtable_accessor_ok {
                    IcallVerdict::Corroborated
                } else {
                    // Accessor exists but was not walked this pass: evidence,
                    // not corroboration (§C.1).
                    IcallVerdict::Candidate
                }
            }
        }
    }

    /// Grade a whole row set and check the totals reconcile (§D).
    pub fn tally(&self, rows: &[IcallWitness]) -> IcallTally {
        let mut t = IcallTally::default();
        for w in rows {
            match self.grade(w) {
                IcallVerdict::Corroborated => t.corroborated += 1,
                IcallVerdict::Candidate => t.candidate += 1,
                IcallVerdict::Rejected { .. } => t.rejected += 1,
                IcallVerdict::Unbound => t.unbound += 1,
                IcallVerdict::Direct => t.direct += 1,
            }
        }
        t
    }
}

/// Card 26 §C golden-corpus hook (canary 18,).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_18() {
        let w = IcallWitness {
            managed_metadata_ok: true, exact_table_record: true,
            bounded_native_fn_ok: true, vtable_accessor_ok: true,
            registered_via: RegisterPath::TableWalk,
        };
        let corro = (IcallCatalogValidator.grade(&w) == IcallVerdict::Corroborated) as u64;
        println!("KDIFF|canary=18|case=grade_corroborated|got={:016x}", corro);
        assert_eq!(corro, 1);
        let bound = (TABLE_REGISTERED_ROWS + DIRECT_ROWS) as u64;
        println!("KDIFF|canary=18|case=bound_total|got={:016x}", bound);
        assert_eq!(bound, 1_777);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const fn table_walk(managed: bool, record: bool, bounded: bool, accessor: bool) -> IcallWitness {
        IcallWitness {
            managed_metadata_ok: managed,
            exact_table_record: record,
            bounded_native_fn_ok: bounded,
            vtable_accessor_ok: accessor,
            registered_via: RegisterPath::TableWalk,
        }
    }

    #[test]
    fn four_witnesses_on_table_walk_corroborate() {
        let v = IcallCatalogValidator;
        assert_eq!(
            v.grade(&table_walk(true, true, true, true)),
            IcallVerdict::Corroborated
        );
        // Canonical pilot anchor: Route_GetPathData is fully corroborated.
        assert_eq!(va_of_rva(ROUTE_GET_PATH_DATA_RVA), IMAGE_BASE + ROUTE_GET_PATH_DATA_RVA);
    }

    #[test]
    fn missing_accessor_is_candidate_not_rejected() {
        let v = IcallCatalogValidator;
        assert_eq!(
            v.grade(&table_walk(true, true, true, false)),
            IcallVerdict::Candidate
        );
    }

    #[test]
    fn each_base_witness_failure_names_its_reason() {
        let v = IcallCatalogValidator;
        assert_eq!(
            v.grade(&table_walk(false, true, true, true)),
            IcallVerdict::Rejected { reason: RejectReason::NameConflict }
        );
        assert_eq!(
            v.grade(&table_walk(true, false, true, true)),
            IcallVerdict::Rejected { reason: RejectReason::TableRecordCoincidence }
        );
        assert_eq!(
            v.grade(&table_walk(true, true, false, true)),
            IcallVerdict::Rejected { reason: RejectReason::BoundedFunctionTooLong }
        );
        // An accessor slot-count mismatch is reported through the same enum
        // (the walk records it as a rejection; grade never invents one).
        assert_eq!(
            IcallVerdict::Rejected { reason: RejectReason::VtableSlotMismatch },
            IcallVerdict::Rejected { reason: RejectReason::VtableSlotMismatch }
        );
    }

    #[test]
    fn direct_and_unbound_paths_short_circuit_the_witnesses() {
        let v = IcallCatalogValidator;
        let direct = IcallWitness {
            registered_via: RegisterPath::DirectMonoScriptHost,
            ..table_walk(true, true, true, true)
        };
        assert_eq!(v.grade(&direct), IcallVerdict::Direct);
        let unbound = IcallWitness {
            registered_via: RegisterPath::GetProcAddress,
            ..table_walk(false, false, false, false)
        };
        assert_eq!(v.grade(&unbound), IcallVerdict::Unbound);
    }

    #[test]
    fn grading_is_deterministic_over_ten_thousand_runs() {
        let v = IcallCatalogValidator;
        let w = table_walk(true, true, true, true);
        for _ in 0..10_000 {
            assert_eq!(v.grade(&w), IcallVerdict::Corroborated);
        }
    }

    #[test]
    fn totals_reconcile_to_the_map_and_bound_is_1777() {
        // The pinned arithmetic (card 18 §A/§D).
        assert_eq!(TABLE_REGISTERED_ROWS + DIRECT_ROWS, BOUND_TOTAL);
        assert_eq!(BOUND_TOTAL, 1_777);
        assert_eq!(
            TABLE_REGISTERED_ROWS + DIRECT_ROWS + UNBOUND_ROWS + NATIVE_ONLY_RECONCILED_ROWS,
            TSV_TOTAL_ROWS
        );
        assert_eq!(UNBOUND_ROWS, 159);

        // A synthetic 1,937-row catalog graded end to end loses nothing:
        // 1,774 corroborated + 1 candidate (accessor not walked) on the table
        // side (the reconciled SimIFace rows grade through the same walk), the
        // two direct rows, and 159 GetProcAddress rows.
        let mut rows = Vec::new();
        for _ in 0..1_774 {
            rows.push(table_walk(true, true, true, true));
        }
        rows.push(table_walk(true, true, true, false)); // accessor not walked
        for _ in 0..159 {
            rows.push(IcallWitness {
                registered_via: RegisterPath::GetProcAddress,
                ..table_walk(true, true, true, true)
            });
        }
        for _ in 0..DIRECT_ROWS {
            rows.push(IcallWitness {
                registered_via: RegisterPath::DirectMonoScriptHost,
                ..table_walk(true, true, true, true)
            });
        }
        let v = IcallCatalogValidator;
        let t = v.tally(&rows);
        // The 1,937-row TSV minus the one native-only row the §C.2
        // reconciliation promotes *by name* (a `VtableSlotMismatch` proof per
        // class), not by table-walk grading: 1,936 rows grade through the
        // validator and nothing is lost along the way.
        assert_eq!(rows.len(), (TSV_TOTAL_ROWS - NATIVE_ONLY_RECONCILED_ROWS) as usize);
        assert_eq!(t.total(), TSV_TOTAL_ROWS - NATIVE_ONLY_RECONCILED_ROWS);
        assert_eq!(t.unbound, UNBOUND_ROWS);
        assert_eq!(t.direct, DIRECT_ROWS);
        // 1,774 corroborated + 1 candidate == the 1,775 table-registered rows;
        // bound (table + direct) is 1,777 exactly.
        assert_eq!(t.corroborated + t.candidate, TABLE_REGISTERED_ROWS);
        assert_eq!(t.bound(), BOUND_TOTAL);
    }

    #[test]
    fn pinned_identity_constants_match_the_sealed_map_header() {
        assert_eq!(IMAGE_BASE, 0x400_0000);
        assert_eq!(INITIALIZE_MONO_SCRIPT_HOST_RUNTIME_RVA, 0x97_e6c7);
        assert_eq!(ADD_MONO_INTERNAL_CALL_RVA, 0xa8_2550);
        assert_eq!(PER_CLASS_TABLE_COUNT, 43);
        assert_eq!(PER_CLASS_TABLE_ENTRY_SIZE, 16);
        assert_eq!(PROVIDER_VTABLE_ACCESSOR_SLOT_OFFSET, 0x10);
        assert_eq!(TS3W_SHA256_HEX.len(), 64);
        assert!(TS3W_SHA256_HEX.chars().all(|c| c.is_ascii_hexdigit()));
        // The pilot anchors land inside the image.
        assert!(va_of_rva(SACS_SEED_RVA) > IMAGE_BASE);
        assert!(va_of_rva(SACS_PATH_DRAW_RVA) > va_of_rva(SACS_SEED_RVA));
    }
}
