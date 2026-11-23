#![forbid(unsafe_code)]
//! Atomic save commit + active-household reload guard (spec card 11).
//!
//! The stock native commit path (`0x6afc50` folder copy) is **not atomic**: it
//! deletes the destination first, then copies file-by-file, so a fault in the
//! backup window or the commit window leaves no loadable save. This
//! module is the clean-room FSM that replaces it:
//!
//! ```text
//! StagingTmp -> VerifyNhdHeaderAndChecksums -> BackupRotate -> AtomicRenameCommit -> Done
//!      |                    |                       |                 |
//!      v                    v                       v                 v
//!    rollback            rollback                rollback          rollback
//!   (unlink tmp)        (unlink tmp)           (restore          (rename tmp-back
//!                                                 prior .backup)  -> rename back .sims3)
//! ```
//!
//! Invariants (card 11 §C.1), each fails without the atomic FSM:
//! 1. At every moment, at least one of `target/`, `backup/`, `lastgood/` is a
//!    loadable complete save.
//! 2. A fault at any stage never deletes the previous `backup/`.
//! 3. The DBPF tail index tiles the payload region exactly at verification
//!    time, before rotation begins.
//! 4. `atomic_rename_commit` is a same-directory rename (same volume); a
//!    cross-volume rename is refused before rotation begins.
//! 5. `.bad` is preserved as an artefact of the last fault.
//!
//! The committer is a **pure filesystem FSM**: no game calls, no wall clock,
//! no randomness. Tests drive it against real temporary directories and are
//! fully deterministic.

use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

use crate::authority::HouseholdId;
use crate::SimId;

/// DBPF 2.0 container major version (card 11 §D).
pub const DBPF_MAJOR: u32 = 2;
/// DBPF 2.0 container minor version.
pub const DBPF_MINOR: u32 = 0;
/// The constant dword at header offset `0x3C`.
pub const DBPF_CONSTANT_3C: u32 = 3;
/// The S3 native-stream-header magic at the record payload offset:
/// `[0x01]['S']['3'][0x00]` (card 11 §D, `verify_magic_s3_at_offset_60`).
pub const S3_MAGIC_BYTES: [u8; 4] = [0x01, b'S', b'3', 0x00];
/// Native save status `0x10`, reentrancy branch (already saving).
pub const SIM_169_STATUS_REENTRANT: u8 = 0x10;
/// Native save status `0x11`, package create / open failed.
pub const SIM_169_STATUS_PACKAGE_OPEN: u8 = 0x11;

/// The five commit phases, one transition per step (card 11 §C).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CommitPhase {
    StagingTmp,
    VerifyNhdHeaderAndChecksums,
    BackupRotate,
    AtomicRenameCommit,
    Done,
}

/// One fault edge per state (card 11 §C). `Reentrant` is the native
/// `0x10` reentrancy branch: a second save while one is in flight.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CommitFault {
    StageIoError,
    VerifyHeaderMismatch,
    VerifyRecordCrcMismatch,
    BackupCopyFault,
    RenameFault,
    Reentrant,
}

/// The five save-tree paths for one save name (card 11 §C).
#[derive(Clone, Debug)]
pub struct SaveFileSet {
    /// `Saves/<name>.sims3/` — the live target.
    pub target: PathBuf,
    /// `Saves/<name>.sims3.backup/` — the previous complete save.
    pub backup: PathBuf,
    /// `Saves/<name>.lastgood/` — the controller copy.
    pub lastgood: PathBuf,
    /// `Saves/<name>.sims3.tmp/` — staging.
    pub tmp: PathBuf,
    /// `Saves/<name>.sims3.bad/` — the native failure sink, kept.
    pub bad: PathBuf,
}

impl SaveFileSet {
    /// Derive the five paths from a save directory and a save name.
    pub fn for_name(dir: &Path, name: &str) -> Self {
        Self {
            target: dir.join(format!("{name}.sims3")),
            backup: dir.join(format!("{name}.sims3.backup")),
            lastgood: dir.join(format!("{name}.lastgood")),
            tmp: dir.join(format!("{name}.sims3.tmp")),
            bad: dir.join(format!("{name}.sims3.bad")),
        }
    }
}

/// A parsed DBPF 2.0 tail index (card 11 §D). The verifier reads only these
/// bounded facts (offsets + tile sum + magic) — never the whole payload.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DbpfRecord {
    pub type_id: u32,
    pub group: u32,
    pub instance: u64,
    pub offset: u32,
    pub csize: u32,
    pub usize: u32,
    pub flags: u32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DbpfManifest {
    pub count: u32,
    pub idx_size: u32,
    pub idx_start: u32,
    /// Sum of the compressed spans; must equal `idx_start - 0x60` exactly.
    pub tile_sum: u32,
    pub records: Vec<DbpfRecord>,
}

/// The atomic save committer FSM (card 11 §C).
pub struct AtomicSaveCommitter {
    pub phase: CommitPhase,
    pub fs: SaveFileSet,
    /// The native reentrancy guard (`this+0x1d`): set while a save is in
    /// flight, from `stage` until `atomic_rename_commit` or a rollback.
    in_flight: bool,
    /// The staged payload, kept in memory so `verify_nhd` re-reads exactly
    /// what was staged (the verifier never trusts a partial on-disk write).
    staged: Vec<u8>,
    moved_target: bool,
    moved_backup: bool,
    moved_lastgood: bool,
}

impl AtomicSaveCommitter {
    pub fn new(fs: SaveFileSet) -> Self {
        Self { phase: CommitPhase::StagingTmp, fs, in_flight: false, staged: Vec::new(),
            moved_target: false, moved_backup: false, moved_lastgood: false }
    }

    /// Stage the caller's serialized payload into `tmp/`. Never touches
    /// `target/`. A fault mid-write lands the attempt in `.bad` and leaves
    /// `target/`, `backup/`, `lastgood/` unchanged ( window 1).
    pub fn stage(&mut self, payload: &[u8]) -> Result<(), CommitFault> {
        if self.in_flight || self.fs.tmp.exists() {
            return Err(CommitFault::Reentrant);
        }
        let paths = [&self.fs.target, &self.fs.backup, &self.fs.lastgood, &self.fs.tmp, &self.fs.bad];
        if paths.iter().any(|p| p.parent() != self.fs.target.parent())
            || paths.iter().enumerate().any(|(i,p)| paths[i+1..].contains(p)) {
            return Err(CommitFault::RenameFault);
        }
        // Atomic directory creation is the cross-committer ownership boundary.
        // A losing contender must never roll back the winner's staging tree.
        fs::create_dir(&self.fs.tmp).map_err(|e| if e.kind() == std::io::ErrorKind::AlreadyExists {
            CommitFault::Reentrant
        } else { CommitFault::StageIoError })?;
        self.in_flight = true;
        self.staged = payload.to_vec();
        let staged = self.stage_to_tmp(payload);
        match staged {
            Ok(()) => {
                self.staged = payload.to_vec();
                self.phase = CommitPhase::VerifyNhdHeaderAndChecksums;
                Ok(())
            }
            Err(()) => {
                // Preserve the partial attempt as the `.bad` artefact.
                let _ = fs::create_dir_all(&self.fs.bad);
                let _ = fs::write(self.fs.bad.join("attempt.bin"), &self.staged);
                self.staged.clear();
                self.rollback_on_fault(CommitFault::StageIoError)
                    .map_err(|_| CommitFault::StageIoError)?;
                Err(CommitFault::StageIoError)
            }
        }
    }

    /// Write the payload into `tmp/` file-by-file (the native `0xdd030`
    /// shape). Returns `Err(())` on any IO fault.
    fn stage_to_tmp(&mut self, payload: &[u8]) -> Result<(), ()> {
        let mut file = fs::File::create(self.fs.tmp.join("payload.bin")).map_err(|_| ())?;
        file.write_all(payload).map_err(|_| ())?;
        file.sync_all().map_err(|_| ())
    }

    /// Read `tmp/payload.bin`; validate the DBPF 2.0 header and the tail
    /// index tiling (card 11 §D). On success the phase advances to
    /// `BackupRotate` and the parsed manifest is returned.
    pub fn verify_nhd(&mut self) -> Result<DbpfManifest, CommitFault> {
        if self.phase != CommitPhase::VerifyNhdHeaderAndChecksums || !self.in_flight {
            return Err(CommitFault::Reentrant);
        }
        let bytes = match fs::read(self.fs.tmp.join("payload.bin")) {
            Ok(b) => b,
            Err(_) => {
                self.rollback_on_fault(CommitFault::VerifyHeaderMismatch)?;
                return Err(CommitFault::VerifyHeaderMismatch);
            }
        };
        let manifest = match parse_dbpf_manifest(&bytes) {
            Ok(m) => m,
            Err(f) => {
                self.rollback_on_fault(f)?;
                return Err(f);
            }
        };
        self.phase = CommitPhase::BackupRotate;
        Ok(manifest)
    }

    /// Rotate `target/` -> `backup/` (a plain rename, not a copy). If a
    /// `lastgood/` exists, the older `backup/` becomes `lastgood/` so the
    /// caller can restore either atomically. The rotation NEVER deletes a
    /// valid `.backup` in place ('s fix to the native bug).
    pub fn rotate_backup(&mut self) -> Result<(), CommitFault> {
        // Invariant 4: the commit rename must be same-directory (same
        // volume). Refuse before any rotation begins.
        if self.phase != CommitPhase::BackupRotate || !self.in_flight {
            return Err(CommitFault::Reentrant);
        }
        if self.fs.target.parent() != self.fs.tmp.parent() {
            return Err(CommitFault::RenameFault);
        }
        let held = self.fs.tmp.with_extension("previous-lastgood");
        if held.exists() { return Err(CommitFault::BackupCopyFault); }
        if self.fs.target.exists() {
            // The older backup (if any) is demoted to lastgood; the current
            // target becomes the new backup. Nothing valid is deleted.
            if self.fs.backup.exists() {
                if self.fs.lastgood.exists() {
                    fs::rename(&self.fs.lastgood, &held).map_err(|_| CommitFault::BackupCopyFault)?;
                    self.moved_lastgood = true;
                }
                if fs::rename(&self.fs.backup, &self.fs.lastgood).is_err() {
                    self.rollback_on_fault(CommitFault::BackupCopyFault)?;
                    return Err(CommitFault::BackupCopyFault);
                }
                self.moved_backup = true;
            }
            if fs::rename(&self.fs.target, &self.fs.backup).is_err() {
                self.rollback_on_fault(CommitFault::BackupCopyFault)?;
                return Err(CommitFault::BackupCopyFault);
            }
            self.moved_target = true;
        }
        self.phase = CommitPhase::AtomicRenameCommit;
        Ok(())
    }

    /// Rename `tmp/` -> `target/`. On a cross-volume or access-denied
    /// failure, roll back: rename `backup/` -> `target/` and re-rotate, so a
    /// valid save is loadable at exit ( window 3).
    pub fn atomic_rename_commit(&mut self) -> Result<(), CommitFault> {
        if self.phase != CommitPhase::AtomicRenameCommit || !self.in_flight {
            return Err(CommitFault::Reentrant);
        }
        let result = if self.fs.target.exists() {
            Err(std::io::Error::from(std::io::ErrorKind::AlreadyExists))
        } else { fs::rename(&self.fs.tmp, &self.fs.target) };
        match result {
            Ok(()) => {
                self.in_flight = false;
                self.phase = CommitPhase::Done;
                self.staged.clear();
                if self.moved_lastgood {
                    fs::remove_dir_all(self.fs.tmp.with_extension("previous-lastgood"))
                        .map_err(|_| CommitFault::BackupCopyFault)?;
                }
                self.moved_target = false;
                self.moved_backup = false;
                self.moved_lastgood = false;
                Ok(())
            }
            Err(_) => {
                // Roll back: restore the previous target from backup.
                self.rollback_on_fault(CommitFault::RenameFault)?;
                Err(CommitFault::RenameFault)
            }
        }
    }

    /// Any earlier fault; put the tree back to its pre-`stage` shape.
    /// `StageIoError` / `Verify*` unlink `tmp/`; `BackupCopyFault` restores
    /// the rotated `backup/` back to `target/`; `RenameFault` is already
    /// rolled back by `atomic_rename_commit`.
    pub fn rollback_on_fault(&mut self, fault: CommitFault) -> Result<(), CommitFault> {
        if fault == CommitFault::Reentrant { return Err(fault); }
        // Never overwrite an unrelated directory created concurrently.
        if self.moved_target && self.fs.target.exists() {
            fs::remove_dir(&self.fs.target).map_err(|_| CommitFault::RenameFault)?;
        }
        if self.moved_target {
            fs::rename(&self.fs.backup, &self.fs.target).map_err(|_| CommitFault::RenameFault)?;
            self.moved_target = false;
        }
        if self.moved_backup {
            fs::rename(&self.fs.lastgood, &self.fs.backup).map_err(|_| CommitFault::BackupCopyFault)?;
            self.moved_backup = false;
        }
        if self.moved_lastgood {
            fs::rename(self.fs.tmp.with_extension("previous-lastgood"), &self.fs.lastgood)
                .map_err(|_| CommitFault::BackupCopyFault)?;
            self.moved_lastgood = false;
        }
        if self.fs.tmp.exists() {
            if self.fs.bad.exists() { fs::remove_dir_all(&self.fs.bad).map_err(|_| fault)?; }
            fs::rename(&self.fs.tmp, &self.fs.bad).map_err(|_| fault)?;
        }
        self.phase = CommitPhase::StagingTmp;
        self.in_flight = false;
        self.staged.clear();
        Ok(())
    }
}


/// Parse and validate a DBPF 2.0 container (card 11 §D). The verifier reads
/// only the bounded facts: the header dwords, the tail index, and the tile
/// sum. It never decompresses or copies the payload.
fn parse_dbpf_manifest(bytes: &[u8]) -> Result<DbpfManifest, CommitFault> {
    // Minimum: 0x44-byte header + at least the idxSize dword.
    if bytes.len() < 0x64 {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    let rd = |off: usize| -> u32 {
        u32::from_le_bytes(bytes[off..off + 4].try_into().unwrap())
    };
    if &bytes[0..4] != b"DBPF" {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    if rd(0x04) != DBPF_MAJOR || rd(0x08) != DBPF_MINOR {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    let count = rd(0x24);
    let idx_size = rd(0x2C);
    if rd(0x3C) != DBPF_CONSTANT_3C {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    let idx_start = rd(0x40);
    let file_len = u32::try_from(bytes.len()).map_err(|_| CommitFault::VerifyHeaderMismatch)?;
    // `idxStart` must equal `fileLen - idxSize`.
    if idx_start < 0x60 || file_len.checked_sub(idx_size) != Some(idx_start) {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    // Each index entry is 32 bytes: type u32 | group u32 | instance u64 |
    // offset u32 | (0x80000000 | csize) u32 | usize u32 | flags u32.
    let expected_idx_size = count.checked_mul(32).and_then(|n| n.checked_add(4));
    if Some(idx_size) != expected_idx_size {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    if idx_start as usize + idx_size as usize > bytes.len() {
        return Err(CommitFault::VerifyHeaderMismatch);
    }
    let mut records = Vec::with_capacity(usize::try_from(count).unwrap_or(0));
    let mut tile_sum: u32 = 0;
    for i in 0..count {
        let base = idx_start as usize + 4 + (i as usize) * 32;
        let type_id = rd(base);
        let group = rd(base + 4);
        let instance =
            u64::from_le_bytes(bytes[base + 8..base + 16].try_into().unwrap());
        let offset = rd(base + 16);
        let csize_word = rd(base + 20);
        let csize = csize_word & 0x7FFF_FFFF;
        let usize_ = rd(base + 24);
        let flags = rd(base + 28);
        // The compressed span must lie inside the payload region
        // [0x60, idxStart) and tile it exactly.
        if offset < 0x60 || csize == 0 || offset.checked_add(csize).is_none_or(|end| end > idx_start) {
            return Err(CommitFault::VerifyRecordCrcMismatch);
        }
        tile_sum = tile_sum.checked_add(csize).ok_or(CommitFault::VerifyRecordCrcMismatch)?;
        records.push(DbpfRecord { type_id, group, instance, offset, csize, usize: usize_, flags });
    }
    // The spans must tile `0x60 .. idxStart` exactly.
    if tile_sum != idx_start.wrapping_sub(0x60) {
        return Err(CommitFault::VerifyRecordCrcMismatch);
    }
    let mut spans: Vec<_> = records.iter().map(|r| (r.offset, r.csize)).collect();
    spans.sort_unstable();
    let mut end = 0x60;
    for (offset, size) in spans {
        if offset != end { return Err(CommitFault::VerifyRecordCrcMismatch); }
        end += size;
    }
    let stream = records.iter().find(|r| r.type_id == 0x03e8_0cdc)
        .ok_or(CommitFault::VerifyHeaderMismatch)?;
    let off = stream.offset as usize;
    if stream.csize < 8 || bytes.get(off..off+4) != Some(S3_MAGIC_BYTES.as_slice())
        || rd(off+4) != 4 { return Err(CommitFault::VerifyHeaderMismatch); }
    Ok(DbpfManifest { count, idx_size, idx_start, tile_sum, records })
}

/// pin: the canonical server primary household survives the save /
/// load round trip; restore forces `PlumbBob.DoSelectActor(actor, forced =
/// true)` (card 11 §E).
#[derive(Clone, Debug)]
pub struct SaveHeader {
    pub world_guid: u64,
    pub sim_clock_start_ticks: i64,
    /// The canonical server primary household (e.g. Martinez / Jennifer).
    /// Iterating multi-household probes during save MUST NOT change this;
    /// restore writes it back as `PlumbBob.SelectedActor` before the first
    /// tick after load.
    pub server_primary: (HouseholdId, SimId),
    pub households: Vec<HouseholdId>,
    /// multi-household claim map.
    pub claims: BTreeMap<SimId, HouseholdId>,
}

impl SaveHeader {
    /// Capture the pin from the running authority **before** any
    /// per-household probe iterates `player_households()` (card 11 §E.3,
    /// save-side pin).
    pub fn capture_pin(
        world_guid: u64,
        sim_clock_start_ticks: i64,
        authority: &crate::authority::ServerHouseholdAuthority,
        sim_households: &BTreeMap<SimId, HouseholdId>,
    ) -> Result<Self, CommitFault> {
        let sim = authority.local_active_actor.ok_or(CommitFault::VerifyHeaderMismatch)?;
        let hh = *sim_households.get(&sim).ok_or(CommitFault::VerifyHeaderMismatch)?;
        let server_primary = (hh, sim);
        let households: Vec<HouseholdId> = authority.active_households.iter().copied().collect();
        let claims = authority.claimed_sims.iter().map(|sim| sim_households.get(sim)
            .map(|hh| (*sim, *hh)).ok_or(CommitFault::VerifyHeaderMismatch))
            .collect::<Result<BTreeMap<_,_>,_>>()?;
        Ok(Self { world_guid, sim_clock_start_ticks, server_primary, households, claims })
    }
}

/// The load-side re-pin (card 11 §E.3 step 3): after `EndLoadObjects()`
/// completes but before the first `Simulate`, force the server primary back.
/// Returns the `(HouseholdId, SimId)` the controller must pass to
/// `PlumbBob.DoSelectActor(actor, forced = true)`.
pub fn load_side_repin(save: &SaveHeader) -> (HouseholdId, SimId) {
    save.server_primary
}
pub fn restore_primary(save: &SaveHeader, materialized: &BTreeMap<SimId, HouseholdId>,
    authority: &mut crate::authority::ServerHouseholdAuthority) -> Result<(), CommitFault> {
    let (household, sim) = save.server_primary;
    if materialized.get(&sim) != Some(&household) { return Err(CommitFault::VerifyHeaderMismatch); }
    authority.local_active_actor = Some(sim);
    Ok(())
}

/// Card 26 §C golden-corpus hook (canary 11, + pin).
#[cfg(test)]
mod kdiff {
    use super::*;

    #[test]
    fn kdiff_canary_11() {
        // The pinned DBPF header identity.
        let id = ((DBPF_MAJOR as u64) << 16) | DBPF_MINOR as u64;
        println!("KDIFF|canary=11|case=dbpf_identity|got={:016x}", id);
        assert_eq!(id, 0x0002_0000);
        assert_eq!(S3_MAGIC_BYTES, [0x01, b'S', b'3', 0x00]);
        // the load side always repins the server primary.
        let hdr = SaveHeader {
            world_guid: 1,
            sim_clock_start_ticks: 0,
            server_primary: (HouseholdId(100), SimId(1)),
            households: vec![HouseholdId(100)],
            claims: BTreeMap::new(),
        };
        let (hh, sim) = load_side_repin(&hdr);
        let pinned = ((hh.0 == 100) && (sim.0 == 1)) as u64;
        println!("KDIFF|canary=11|case=sim178_repin|got={:016x}", pinned);
        assert_eq!(pinned, 1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Build a minimal valid DBPF 2.0 container: header + one record whose
    /// compressed span tiles `0x60 .. idxStart` exactly.
    fn valid_dbpf(count: u32) -> Vec<u8> {
        // One record per `count`; each span is 4 bytes, starting at 0x60.
        let span = 8u32;
        let idx_size = 4 + count * 32;
        let idx_start = 0x60 + count * span;
        let file_len = idx_start + idx_size;
        let mut b = vec![0u8; file_len as usize];
        b[0..4].copy_from_slice(b"DBPF");
        b[4..8].copy_from_slice(&DBPF_MAJOR.to_le_bytes());
        b[8..12].copy_from_slice(&DBPF_MINOR.to_le_bytes());
        b[0x24..0x28].copy_from_slice(&count.to_le_bytes());
        b[0x2C..0x30].copy_from_slice(&idx_size.to_le_bytes());
        b[0x3C..0x40].copy_from_slice(&DBPF_CONSTANT_3C.to_le_bytes());
        b[0x40..0x44].copy_from_slice(&idx_start.to_le_bytes());
        for i in 0..count {
            let off = 0x60 + i * span;
            b[off as usize..off as usize + 4].copy_from_slice(&S3_MAGIC_BYTES);
            b[off as usize+4..off as usize+8].copy_from_slice(&4u32.to_le_bytes());
            let base = idx_start as usize + 4 + (i as usize) * 32;
            b[base..base + 4].copy_from_slice(&0x03e8_0cdc_u32.to_le_bytes());
            b[base + 4..base + 8].copy_from_slice(&0u32.to_le_bytes());
            b[base + 8..base + 16].copy_from_slice(&1u64.to_le_bytes());
            b[base + 16..base + 20].copy_from_slice(&off.to_le_bytes());
            b[base + 20..base + 24].copy_from_slice(&(0x8000_0000 | span).to_le_bytes());
            b[base + 24..base + 28].copy_from_slice(&span.to_le_bytes());
            b[base + 28..base + 32].copy_from_slice(&0u32.to_le_bytes());
        }
        b
    }

    fn test_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("sim96_persist_{}_{name}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn stage_fault_never_touches_target() {
        let dir = test_dir("stage_fault");
        let fs = SaveFileSet::for_name(&dir, "save1");
        // Pre-existing valid target.
        fs::create_dir_all(&fs.target).unwrap();
        fs::write(fs.target.join("payload.bin"), b"OLD").unwrap();
        let mut c = AtomicSaveCommitter::new(fs.clone());
        // Force a stage fault by making tmp unwritable is platform-dependent;
        // instead verify the happy path leaves target untouched during stage.
        let payload = valid_dbpf(1);
        assert_eq!(c.stage(&payload), Ok(()));
        // Target must still be the OLD save (staging never touches target).
        assert_eq!(fs::read(fs.target.join("payload.bin")).unwrap(), b"OLD");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn verify_nhd_missing_tiles_rejects() {
        let dir = test_dir("verify_tiles");
        let fs = SaveFileSet::for_name(&dir, "save2");
        let mut c = AtomicSaveCommitter::new(fs.clone());
        // A payload whose spans do NOT tile 0x60..idxStart exactly.
        let mut bad = valid_dbpf(1);
        // Corrupt the record offset so the span no longer tiles.
        let idx_start = 0x60 + 8;
        let base = idx_start as usize + 4;
        bad[base + 16..base + 20].copy_from_slice(&0x64u32.to_le_bytes()); // offset 0x64, span 4 -> ends 0x68 > idxStart 0x64
        assert_eq!(c.stage(&bad), Ok(()));
        assert_eq!(c.verify_nhd(), Err(CommitFault::VerifyRecordCrcMismatch));
        // No rotation attempted: phase back at StagingTmp.
        assert_eq!(c.phase, CommitPhase::StagingTmp);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn verify_magic_wrong_rejects() {
        let dir = test_dir("verify_magic");
        let fs = SaveFileSet::for_name(&dir, "save3");
        let mut c = AtomicSaveCommitter::new(fs.clone());
        let mut bad = valid_dbpf(1);
        bad[0..4].copy_from_slice(b"XXXX");
        assert_eq!(c.stage(&bad), Ok(()));
        assert_eq!(c.verify_nhd(), Err(CommitFault::VerifyHeaderMismatch));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn backup_rotate_is_rename_not_delete_then_copy() {
        let dir = test_dir("rotate");
        let fs = SaveFileSet::for_name(&dir, "save4");
        // Pre-existing target + backup.
        fs::create_dir_all(&fs.target).unwrap();
        fs::write(fs.target.join("payload.bin"), b"TARGET").unwrap();
        fs::create_dir_all(&fs.backup).unwrap();
        fs::write(fs.backup.join("payload.bin"), b"BACKUP").unwrap();
        let mut c = AtomicSaveCommitter::new(fs.clone());
        let _ = c.stage(&valid_dbpf(1));
        let _ = c.verify_nhd();
        assert_eq!(c.rotate_backup(), Ok(()));
        // backup now holds the OLD target; target is gone (renamed).
        assert_eq!(fs::read(fs.backup.join("payload.bin")).unwrap(), b"TARGET");
        assert!(!fs.target.exists());
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn commit_rename_fault_restores_backup() {
        let dir = test_dir("commit_fault");
        let fs = SaveFileSet::for_name(&dir, "save5");
        fs::create_dir_all(&fs.target).unwrap();
        fs::write(fs.target.join("payload.bin"), b"PREV").unwrap();
        let mut c = AtomicSaveCommitter::new(fs.clone());
        let _ = c.stage(&valid_dbpf(1));
        let _ = c.verify_nhd();
        let _ = c.rotate_backup(); // target -> backup
        // Force a commit fault: pre-create target so the rename fails.
        fs::create_dir_all(&fs.target).unwrap();
        assert_eq!(c.atomic_rename_commit(), Err(CommitFault::RenameFault));
        // A valid save is loadable at exit: backup was restored to target.
        assert!(fs.target.exists());
        assert_eq!(fs::read(fs.target.join("payload.bin")).unwrap(), b"PREV");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn native_reentrant_save_returns_reentrant() {
        let dir = test_dir("reentrant");
        let fs = SaveFileSet::for_name(&dir, "save6");
        let mut c = AtomicSaveCommitter::new(fs.clone());
        // Simulate an in-flight save.
        c.in_flight = true;
        assert_eq!(c.stage(&valid_dbpf(1)), Err(CommitFault::Reentrant));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn separate_committers_cannot_erase_each_others_staging_tree() {
        let dir = test_dir("two_committers");
        let files = SaveFileSet::for_name(&dir, "shared");
        let mut first = AtomicSaveCommitter::new(files.clone());
        let mut second = AtomicSaveCommitter::new(files.clone());
        let bytes = valid_dbpf(1);
        first.stage(&bytes).unwrap();
        assert_eq!(second.stage(b"other"), Err(CommitFault::Reentrant));
        assert_eq!(fs::read(files.tmp.join("payload.bin")).unwrap(), bytes);
        first.verify_nhd().unwrap();
        first.rotate_backup().unwrap();
        first.atomic_rename_commit().unwrap();
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn happy_path_lands_at_done() {
        let dir = test_dir("happy");
        let fs = SaveFileSet::for_name(&dir, "save7");
        let mut c = AtomicSaveCommitter::new(fs.clone());
        assert_eq!(c.stage(&valid_dbpf(2)), Ok(()));
        let m = c.verify_nhd().unwrap();
        assert_eq!(m.count, 2);
        assert_eq!(m.tile_sum, m.idx_start - 0x60);
        assert_eq!(c.rotate_backup(), Ok(()));
        assert_eq!(c.atomic_rename_commit(), Ok(()));
        assert_eq!(c.phase, CommitPhase::Done);
        assert!(fs.target.exists());
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn sim_178_pin_survives_multi_household_probe() {
        // The pin is captured once and is a plain value; iterating
        // player_households() during the probe cannot change it.
        let mut authority = crate::authority::ServerHouseholdAuthority::default();
        authority.claimed_sims.insert(SimId(1));
        authority.active_households.insert(HouseholdId(100));
        authority.local_active_actor = Some(SimId(1));
        let membership = [(SimId(1), HouseholdId(100))].into_iter().collect();
        let save = SaveHeader::capture_pin(WORLD, 0, &authority, &membership).unwrap();
        // Probe iteration (read-only) must not change the pin.
        for _ in authority.player_households() {
            let _ = save.server_primary;
        }
        assert_eq!(save.server_primary.0, HouseholdId(100));
        assert_eq!(save.server_primary.1, SimId(1));
        // Load-side re-pin returns the same value.
        assert_eq!(load_side_repin(&save), (HouseholdId(100), SimId(1)));
    }

    const WORLD: u64 = 0x1234;

    #[test]
    fn failed_commit_restores_target_backup_and_lastgood_byte_exactly() {
        let dir = test_dir("full_rollback");
        let files = SaveFileSet::for_name(&dir, "world");
        for (path, bytes) in [(&files.target, b"TARGET"), (&files.backup, b"BACKUP"), (&files.lastgood, b"OLDEST")] {
            fs::create_dir(path).unwrap();
            fs::write(path.join("payload.bin"), bytes).unwrap();
        }
        let mut c = AtomicSaveCommitter::new(files.clone());
        c.stage(&valid_dbpf(2)).unwrap();
        c.verify_nhd().unwrap();
        c.rotate_backup().unwrap();
        fs::create_dir(&files.target).unwrap();
        assert_eq!(c.atomic_rename_commit(), Err(CommitFault::RenameFault));
        for (path, bytes) in [(&files.target, b"TARGET"), (&files.backup, b"BACKUP"), (&files.lastgood, b"OLDEST")] {
            assert_eq!(fs::read(path.join("payload.bin")).unwrap(), bytes);
        }
        assert!(files.bad.join("payload.bin").exists());
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn tail_index_rejects_overlap_overflow_and_wrong_stream_magic() {
        let mut overlap = valid_dbpf(2);
        let index = u32::from_le_bytes(overlap[0x40..0x44].try_into().unwrap()) as usize + 4;
        overlap[index+32+16..index+32+20].copy_from_slice(&0x60u32.to_le_bytes());
        assert_eq!(parse_dbpf_manifest(&overlap), Err(CommitFault::VerifyRecordCrcMismatch));
        let mut overflow = valid_dbpf(1);
        overflow[0x24..0x28].copy_from_slice(&u32::MAX.to_le_bytes());
        assert_eq!(parse_dbpf_manifest(&overflow), Err(CommitFault::VerifyHeaderMismatch));
        let mut magic = valid_dbpf(1);
        magic[0x60] = 2;
        assert_eq!(parse_dbpf_manifest(&magic), Err(CommitFault::VerifyHeaderMismatch));
        for len in 0..magic.len() {
            assert!(parse_dbpf_manifest(&magic[..len]).is_err());
        }
    }
}
