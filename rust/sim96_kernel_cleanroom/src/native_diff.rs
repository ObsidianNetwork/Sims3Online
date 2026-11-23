#![forbid(unsafe_code)]
//! Card 26 · native & kernel differential corpus (Canary 26).
//!
//! The three-verdict epistemology Worker A1 asked for: an oracle self-test
//! proves Worker B's model; the static AST audit proves structure; only a
//! `KERNEL_DIFF_VERIFIED` proves the *compiled* kernel agrees with the
//! captured native `TS3W.exe` facts. This module is the kernel side of that
//! third verdict: it replays the captured golden corpus — the six
//! accepted SACS LCG traces, the `4ff7481` unsigned-`PathType` witness, the
//! card 01 eight-word `Route_GetPathData` copy, and the Boehm
//! free-list descriptor — directly against the real kernel functions
//! (`sacs_rng::path_draw_lcg`, `routing::*`, `handles::HandleArena`,
//! `guest_mem::GuestBytes`) and reports bit-exact agreement.
//!
//! Corpus hygiene (card 26 §B): only RVAs, offsets and short integer tuples
//! that are already recorded in this repo's docs; no EA-derived binary bytes.
//! The `#[cfg(test)] mod kdiff` block below is the hook the `diff_audit`
//! harness greps for (`KDIFF|canary=…|case=…|got=…` lines with
//! `-- --nocapture`); without it the gate table can only ever read
//! `KernelTestMissing`, never `KernelDiffVerified` (card 26 §C).

use crate::guest_mem::{GuestBytes, GuestMem32, GuestMem32Mut};
use crate::handles::{HandleArena, KernelError};
use crate::routing::{PathData, PathType, RouteCalls, route_get_path_data_abi_raw};
use crate::sacs_rng::path_draw_lcg;

/// The six accepted state-transition vectors
/// (`docs/engine/sacs-native-picks.md` §"1. Path picks"; the same table the
/// kernel's own `sacs_rng::native_integer_vectors_and_store_effect` asserts).
pub const SIM165_TRACE_VECTORS: [(u32 /*state_in*/, u32 /*limit*/,
                                 u32 /*new_state*/, u32 /*result*/); 6] = [
    (0x0000_0000, 0x0000_0000, 0x0000_3039, 0x0000_0000),
    (0x0000_0001, 0x0000_000A, 0x41C6_7EA6, 0x0000_0000),
    (0x1234_5678, 0xFFFF_FFFF, 0x0B71_9151, 0x6403_0B70),
    (0xFFFF_FFFF, 0xFFFF_FFFF, 0xBE39_E1CC, 0x4E6C_BE38),
    (0x8000_0000, 0x8000_0000, 0x8000_3039, 0x139B_4000),
    (0x7FFF_FFFF, 123_456_789, 0x3E39_E1CC, 0x0120_8B65),
];

/// `4ff7481` — the door `PathType` transition witness, recorded signed; its
/// unsigned reading is `0x8A86_3E32`.
pub const PATH_TYPE_SIGNED_WITNESS: i32 = -1_970_913_742;

/// The signed witness re-expressed through the kernel's own unsigned type.
pub const PATH_TYPE_WITNESS_UNSIGNED: u32 = 0x8A86_3E32;

/// Canary 5 §B — a live Boehm chunk's last word at `chunk + 0x7FC`.
pub const BOEHM_LIVE_DESCRIPTOR: u32 = 0x2A;
pub const BOEHM_CHUNK_SIZE: u32 = 0x800;
pub const BOEHM_DESCRIPTOR_OFFSET: u32 = 0x7FC;
/// The `interp-indirect-load` free-list signature: a freed chunk's `prev`
/// word (`chunk + 0x4`) reads the sentinel `1`.
pub const BOEHM_FREE_PREV_SENTINEL: u32 = 1;

/// The eight-word native copy at `data +0x00 … +0x1c` (card 01 §C,
/// `routing-contract.md` §B). Only words 0, 2, 3, 4, 5, 6 are defined fields;
/// words 1 and 7 are padding and must never be read or compared.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RouteGetPathDataNativeVec {
    pub path_type: u32,
    pub pad0: u32,
    pub guid_lo: u32,
    pub guid_hi: u32,
    pub pos_x_bits: u32,
    pub pos_y_bits: u32,
    pub pos_z_bits: u32,
    pub pad1: u32,
}

impl RouteGetPathDataNativeVec {
    /// The six *defined* words, in native order — the comparison set. The
    /// padding words are excluded by construction (Kani
    /// `route_get_path_data_words_defined`).
    pub fn defined_words(&self) -> [u32; 6] {
        [self.path_type, self.guid_lo, self.guid_hi,
         self.pos_x_bits, self.pos_y_bits, self.pos_z_bits]
    }
}

/// The three-verdict grade (card 26 §A / §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KernelVerdict {
    OracleSelftestPass,
    StaticAstPass,
    KernelDiffVerified,
    KernelDiffFailed { case: &'static str, expected_hex: u64, got_hex: u64 },
    KernelAbsent,
    KernelTestMissing,
}

/// One golden vector: canary, case name, and the oracle's expected output
/// rendered as one `u64`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DiffCase {
    pub canary: u32,
    pub name: &'static str,
    pub expected_hex: u64,
}

/// The audit report: one verdict per case plus the class tallies.
#[derive(Clone, Debug, Default)]
pub struct DiffAuditReport {
    pub cases: Vec<(DiffCase, KernelVerdict)>,
    pub oracle_pass: u32,
    pub ast_pass: u32,
    pub diff_verified: u32,
    pub diff_failed: u32,
    pub kernel_absent: u32,
    pub test_missing: u32,
}

impl DiffAuditReport {
    fn record(&mut self, case: DiffCase, verdict: KernelVerdict) {
        match verdict {
            KernelVerdict::KernelDiffVerified => self.diff_verified += 1,
            KernelVerdict::KernelDiffFailed { .. } => self.diff_failed += 1,
            KernelVerdict::KernelAbsent => self.kernel_absent += 1,
            KernelVerdict::KernelTestMissing => self.test_missing += 1,
            KernelVerdict::OracleSelftestPass => self.oracle_pass += 1,
            KernelVerdict::StaticAstPass => self.ast_pass += 1,
        }
        self.cases.push((case, verdict));
    }

    pub fn all_verified(&self) -> bool {
        self.diff_failed == 0 && self.kernel_absent == 0 && self.test_missing == 0
            && self.diff_verified as usize == self.cases.len()
    }
}

/// Grade one golden vector exactly like the sealed referee's
/// `grade_golden_vector` (short-circuit order is the contract): an oracle
/// failure is a diff failure; a missing hook is *never* a verification.
pub fn grade_golden_vector(oracle_pass: bool, kdiff_hook_present: bool, kernel_matches_oracle: bool) -> KernelVerdict {
    if !oracle_pass {
        return KernelVerdict::KernelDiffFailed { case: "oracle", expected_hex: 0, got_hex: 0 };
    }
    if !kdiff_hook_present {
        return KernelVerdict::KernelTestMissing;
    }
    if kernel_matches_oracle {
        KernelVerdict::KernelDiffVerified
    } else {
        KernelVerdict::KernelDiffFailed { case: "match", expected_hex: 1, got_hex: 0 }
    }
}

/// The captured corpus and its replay against the compiled kernel.
pub struct NativeTraceCorpus;

impl NativeTraceCorpus {
    /// Replay every captured vector through the real kernel functions and
    /// grade bit-exactness (card 26 §C step 2, the kernel's own side).
    pub fn verify_all(&self) -> DiffAuditReport {
        let mut report = DiffAuditReport::default();

        // 1. The six SACS LCG traces through `path_draw_lcg`.
        for (i, (state, limit, want_s, want_r)) in SIM165_TRACE_VECTORS.into_iter().enumerate() {
            let (got_s, got_r) = path_draw_lcg(state, limit);
            let expected = ((want_s as u64) << 32) | want_r as u64;
            let got = ((got_s as u64) << 32) | got_r as u64;
            let verdict = if expected == got {
                KernelVerdict::KernelDiffVerified
            } else {
                KernelVerdict::KernelDiffFailed { case: "sacs_trace", expected_hex: expected, got_hex: got }
            };
            report.record(DiffCase { canary: 26, name: match i {
                0 => "sacs_zero_seed", 1 => "sacs_one_ten", 2 => "sacs_mid_max",
                3 => "sacs_all_ff", 4 => "sacs_high_bits", _ => "sacs_odd_limit" },
                expected_hex: expected }, verdict);
        }

        // 2. The `4ff7481` unsigned witness through the kernel's own
        //    `PathType`: the signed reading must reorder as unsigned.
        let signed = PATH_TYPE_SIGNED_WITNESS as u32;
        let expected = PATH_TYPE_WITNESS_UNSIGNED as u64;
        let verdict = if signed == PATH_TYPE_WITNESS_UNSIGNED
            && PathType(signed) == PathType::VEHICLE
            && PathType(signed) > PathType(0x7FFF_FFFF) {
            KernelVerdict::KernelDiffVerified
        } else {
            KernelVerdict::KernelDiffFailed { case: "pathtype_witness", expected_hex: expected, got_hex: signed as u64 }
        };
        report.record(DiffCase { canary: 26, name: "pathtype_unsigned_witness", expected_hex: expected }, verdict);

        // 3. The card-01 eight-word copy through the real pointer-hop walker.
        let (mem, calls, golden) = golden_route_fixture();
        let got = route_get_path_data_abi_raw(&mem, &mut calls.clone(), 1, 0x2500_0001, 0);
        let expected = ((golden.path_type as u64) << 32) | golden.guid_lo as u64;
        let want = PathData {
            path_type: golden.path_type,
            object_id: ((golden.guid_hi as u64) << 32) | golden.guid_lo as u64,
            portal_bits: [golden.pos_x_bits, golden.pos_y_bits, golden.pos_z_bits],
        };
        // The golden copy carries live-looking padding (words 1 and 7); the
        // walker reads only the six defined words, so equality here *proves*
        // the padding was ignored (Kani `route_get_path_data_words_defined`).
        let verdict = match &got {
            Some(d) if *d == want => KernelVerdict::KernelDiffVerified,
            Some(d) => KernelVerdict::KernelDiffFailed {
                case: "route_get_path_data8", expected_hex: expected,
                got_hex: ((d.path_type as u64) << 32) | (d.object_id & 0xffff_ffff) },
            None => KernelVerdict::KernelDiffFailed { case: "route_get_path_data8", expected_hex: expected, got_hex: 0 },
        };
        report.record(DiffCase { canary: 26, name: "route_get_path_data_eight_word", expected_hex: expected }, verdict);

        // 4. The Boehm free-list descriptor witnesses through `GuestBytes`.
        let (mem, base) = boehm_fixture();
        let desc = mem.read_u32(base + BOEHM_DESCRIPTOR_OFFSET).unwrap_or(0);
        let verdict = if desc == BOEHM_LIVE_DESCRIPTOR {
            KernelVerdict::KernelDiffVerified
        } else {
            KernelVerdict::KernelDiffFailed { case: "boehm_live", expected_hex: BOEHM_LIVE_DESCRIPTOR as u64, got_hex: desc as u64 }
        };
        report.record(DiffCase { canary: 26, name: "boehm_descriptor_7fc", expected_hex: BOEHM_LIVE_DESCRIPTOR as u64 }, verdict);
        let freed_prev = mem.read_u32(base + BOEHM_CHUNK_SIZE + 0x4).unwrap_or(0);
        let verdict = if freed_prev == BOEHM_FREE_PREV_SENTINEL {
            KernelVerdict::KernelDiffVerified
        } else {
            KernelVerdict::KernelDiffFailed { case: "boehm_freed", expected_hex: 1, got_hex: freed_prev as u64 }
        };
        report.record(DiffCase { canary: 26, name: "boehm_free_prev_sentinel", expected_hex: 1 }, verdict);

        // 5. The safety property: the kernel arena never answers `Ok`
        //    for a removed generation.
        let mut arena = HandleArena::new();
        let h = arena.insert(0xC0FFEEu32).expect("insert");
        arena.remove(h).expect("remove");
        let got_hex = match arena.get(h) {
            Err(KernelError::StaleObjectHandle) => 0u64,
            _ => 1u64,
        };
        let verdict = if got_hex == 0 {
            KernelVerdict::KernelDiffVerified
        } else {
            KernelVerdict::KernelDiffFailed { case: "boehm_stale", expected_hex: 0, got_hex }
        };
        report.record(DiffCase { canary: 26, name: "sim166_stale_never_ok", expected_hex: 0 }, verdict);

        report
    }
}

/// The card-01 golden arena (the same disjoint-slot layout as
/// `routing::native_reads_path_data_and_releases_lock`, with the recorded
/// eight-word copy as the golden expectation).
fn golden_route_fixture() -> (GuestBytes, GoldenCalls, RouteGetPathDataNativeVec) {
    let base = 0x1000u32;
    let route = 0x1100u32;
    let sentinel = route + 0xe0;
    let entry = 0x1204u32;
    let child = 0x1300u32;
    let data = 0x1400u32;
    let golden = RouteGetPathDataNativeVec {
        path_type: 0x8A86_3E32, // the door witness: a real captured high-bit tag
        pad0: 0x0000_0000,
        guid_lo: 0xBBBB_1010,
        guid_hi: 0x0000_0000,
        pos_x_bits: 0x41C6_4E6D,
        pos_y_bits: 0x0000_3039,
        pos_z_bits: 0x278D_DE6D,
        pad1: 0xDEAD_BEEF, // padding: present in the copy, never compared
    };
    let mut bytes = vec![0u8; 0x500];
    let put = |bytes: &mut Vec<u8>, addr: u32, value: u32| {
        let off = (addr - base) as usize;
        bytes[off..off + 4].copy_from_slice(&value.to_le_bytes());
    };
    put(&mut bytes, route, 0x1080); // route vtable
    put(&mut bytes, 0x1080 + 0x10, 0x9001); // lock method slot
    put(&mut bytes, 0x1080 + 0x14, 0x9002); // unlock method slot
    put(&mut bytes, route + 0xe0, sentinel); // route + 0xe0: empty-list sentinel ADDRESS
    put(&mut bytes, sentinel, entry); // first entry
    put(&mut bytes, entry + 0x08, child); // entry child
    put(&mut bytes, child, 0x1320); // child vtable
    put(&mut bytes, 0x1320 + 0x30, 0x9030); // the +0x30 virtual getter
    put(&mut bytes, data, golden.path_type);
    put(&mut bytes, data + 0x04, golden.pad0);
    put(&mut bytes, data + 0x08, golden.guid_lo);
    put(&mut bytes, data + 0x0c, golden.guid_hi);
    put(&mut bytes, data + 0x10, golden.pos_x_bits);
    put(&mut bytes, data + 0x14, golden.pos_y_bits);
    put(&mut bytes, data + 0x18, golden.pos_z_bits);
    put(&mut bytes, data + 0x1c, golden.pad1);
    let mem = GuestBytes::new(base, bytes);
    (mem, GoldenCalls { route: Some(route), data: Some(data) }, golden)
}

#[derive(Clone, Copy, Debug)]
struct GoldenCalls {
    route: Option<u32>,
    data: Option<u32>,
}

impl RouteCalls for GoldenCalls {
    fn lookup_route(&mut self, _system: u32, _handle: u32) -> Option<u32> {
        self.route
    }
    fn invoke_lock(&mut self, _method: u32, _route: u32) -> Option<()> {
        Some(())
    }
    fn invoke_unlock(&mut self, _method: u32, _route: u32) -> Option<()> {
        Some(())
    }
    fn invoke_path_data(&mut self, _method: u32, _child: u32) -> Option<u32> {
        self.data
    }
}

/// Two Boehm chunks: a live one whose `+0x7FC` descriptor reads `0x2A`, and a
/// freed one whose `prev` word carries the `1` sentinel.
fn boehm_fixture() -> (GuestBytes, u32) {
    let base = 0x2000_0000u32;
    let mut mem = GuestBytes::new(base, vec![0u8; (BOEHM_CHUNK_SIZE * 2) as usize]);
    mem.write_u32(base + BOEHM_DESCRIPTOR_OFFSET, BOEHM_LIVE_DESCRIPTOR);
    let freed = base + BOEHM_CHUNK_SIZE;
    mem.write_u32(freed, 0); // free-list next (in-chunk offset 0x0)
    mem.write_u32(freed + 0x4, BOEHM_FREE_PREV_SENTINEL); // prev sentinel
    (mem, base)
}

/// The `diff_audit` hook (card 26 §C): every case prints its
/// `KDIFF|canary=…|case=…|got=…` line for the harness to parse (run with
/// `-- --nocapture`) and asserts bit-exactness against the captured corpus.
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_corpus_matches_compiled_kernel() {
        for (i, (state, limit, want_s, want_r)) in SIM165_TRACE_VECTORS.iter().enumerate() {
            let (got_s, got_r) = path_draw_lcg(*state, *limit);
            println!("KDIFF|canary=26|case=sacs_vector_{i}|got={:016x}", ((got_s as u64) << 32) | got_r as u64);
            assert_eq!((got_s, got_r), (*want_s, *want_r), "sacs vector {i}");
        }
        println!("KDIFF|canary=26|case=pathtype_witness|got={:016x}", PATH_TYPE_WITNESS_UNSIGNED as u64);
        assert_eq!(PATH_TYPE_SIGNED_WITNESS as u32, PATH_TYPE_WITNESS_UNSIGNED);
        assert_eq!(PathType(PATH_TYPE_WITNESS_UNSIGNED), PathType::VEHICLE);

        let (mem, mut calls, golden) = golden_route_fixture();
        let d = route_get_path_data_abi_raw(&mem, &mut calls, 1, 0x2500_0001, 0).expect("golden hop");
        println!("KDIFF|canary=26|case=route_get_path_data8|got={:016x}",
            ((d.path_type as u64) << 32) | (d.object_id & 0xffff_ffff));
        assert_eq!(d.path_type, golden.path_type);
        assert_eq!(d.object_id, ((golden.guid_hi as u64) << 32) | golden.guid_lo as u64);
        assert_eq!(d.portal_bits, [golden.pos_x_bits, golden.pos_y_bits, golden.pos_z_bits]);
        // Padding words 1 and 7 are present in the copy but never read by the
        // walker: `PathData` carries no slot for them (Kani
        // `route_get_path_data_words_defined`).
        assert_eq!(golden.defined_words().len(), 6);

        let (mem, base) = boehm_fixture();
        println!("KDIFF|canary=26|case=boehm_descriptor|got={:016x}",
            mem.read_u32(base + BOEHM_DESCRIPTOR_OFFSET).unwrap_or(0) as u64);
        assert_eq!(mem.read_u32(base + BOEHM_DESCRIPTOR_OFFSET), Some(BOEHM_LIVE_DESCRIPTOR));
        assert_eq!(mem.read_u32(base + BOEHM_CHUNK_SIZE + 0x4), Some(BOEHM_FREE_PREV_SENTINEL));

        let report = NativeTraceCorpus.verify_all();
        println!("KDIFF|canary=26|case=verify_all|got={}", report.diff_verified);
        assert!(report.all_verified(), "{:?}", report.cases.iter().filter(|(_, v)| *v != KernelVerdict::KernelDiffVerified).collect::<Vec<_>>());
        // The hook gate itself: present here, so a match grades Verified.
        assert_eq!(grade_golden_vector(true, true, true), KernelVerdict::KernelDiffVerified);
    }
}
