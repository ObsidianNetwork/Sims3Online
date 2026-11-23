#![forbid(unsafe_code)]
#[path = "../src/last_good.rs"]
mod last_good;

use last_good::*;
use sim96_kernel_cleanroom::persistence::{AtomicSaveCommitter, CommitFault, CommitPhase, SaveFileSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

const NOW: &str = "2026-10-21T23:05:00.0000000+00:00";
const COMPACT: &str = "20261021-230500";
const NHD: &str = "Sunset Valley_0x0859db3c.nhd";
static NEXT_TEMP: AtomicU64 = AtomicU64::new(0);

struct Temp(PathBuf);
impl Temp {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "card59-lastgood-{}-{}",
            std::process::id(),
            NEXT_TEMP.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
    fn save(&self) -> PathBuf {
        self.0.join("Option4BookChain.sims3")
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.0).unwrap();
    }
}

// Synthetic oracle fixture: only the C# header/extent requirements, not the stricter kernel model.
fn fake_dbpf(records: u32, payload: usize, fill: u8) -> Vec<u8> {
    let index_size = 4 + 32 * records;
    let mut data = vec![0; 0x60 + payload + index_size as usize];
    data[..4].copy_from_slice(b"DBPF");
    data[4..8].copy_from_slice(&2u32.to_le_bytes());
    data[0x24..0x28].copy_from_slice(&records.to_le_bytes());
    data[0x2c..0x30].copy_from_slice(&index_size.to_le_bytes());
    data[0x40..0x44].copy_from_slice(&(0x60u32 + payload as u32).to_le_bytes());
    data[0x60..0x60 + payload].fill(fill);
    data
}

fn fake_save(fill: u8) -> Vec<(String, Vec<u8>)> {
    vec![
        ("1a5tf1l3.dat".into(), b"Sunset Valley_0x0859db3c".to_vec()),
        ("Meta.data".into(), fake_dbpf(1, 64, fill)),
        (NHD.into(), fake_dbpf(12, 4096, fill)),
        ("TravelDB.package".into(), fake_dbpf(3, 256, fill)),
    ]
}

fn write_save(folder: &Path, files: &[(String, Vec<u8>)]) {
    fs::create_dir_all(folder).unwrap();
    for (name, data) in files {
        fs::write(folder.join(name), data).unwrap();
    }
}

fn kernel_dbpf() -> Vec<u8> {
    let mut bytes = fake_dbpf(1, 8, 0);
    bytes[0x3c..0x40].copy_from_slice(&3u32.to_le_bytes());
    bytes[0x60..0x64].copy_from_slice(&[1, b'S', b'3', 0]);
    bytes[0x64..0x68].copy_from_slice(&4u32.to_le_bytes());
    let record = 0x68 + 4;
    bytes[record..record + 4].copy_from_slice(&0x03e8_0cdcu32.to_le_bytes());
    bytes[record + 16..record + 20].copy_from_slice(&0x60u32.to_le_bytes());
    bytes[record + 20..record + 24].copy_from_slice(&0x8000_0008u32.to_le_bytes());
    bytes[record + 24..record + 28].copy_from_slice(&8u32.to_le_bytes());
    bytes
}

#[test]
fn sha256_known_vectors_and_padding_boundaries() {
    for (input, expected) in [
        (Vec::new(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
        (b"abc".to_vec(), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
        (b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq".to_vec(), "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"),
        (vec![b'a'; 1_000_000], "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"),
    ] {
        assert_eq!(sha256_hex(&input), expected);
    }
}

#[test]
fn problem_finds_cut_missing_empty_and_invalid_dbpf_files() {
    let files = fake_save(7);
    assert_eq!(problem(&files), None);
    let mut cut = files.clone();
    let cut_length = cut[2].1.len() - 100;
    cut[2].1.truncate(cut_length);
    assert!(problem(&cut).unwrap().starts_with(&format!("{NHD}: DBPF index ends at")));
    assert_eq!(problem(&files[..2]), Some("no .nhd file".into()));
    let mut empty = files.clone();
    empty[0].1.clear();
    assert_eq!(problem(&empty), Some("1a5tf1l3.dat is empty".into()));
    let mut bad_count = fake_dbpf(12, 4096, 7);
    bad_count[0x24..0x28].copy_from_slice(&13u32.to_le_bytes());
    assert!(dbpf_problem(&bad_count).unwrap().contains("does not fit 13 records"));
    assert_eq!(dbpf_problem(b"plain text"), None);
    assert_eq!(dbpf_problem(b"DBP"), None);
    assert_eq!(dbpf_problem(b"DBPF"), Some("DBPF header cut short (4 bytes)".into()));
    let mut markers = files;
    markers.push((MANIFEST_FILE_NAME.into(), vec![]));
    markers.push((WORLD_MARKER_FILE_NAME.into(), vec![]));
    markers[2].0 = NHD.to_ascii_uppercase();
    assert_eq!(problem(&markers), None);
}

#[test]
fn manifest_preserves_spaces_crlf_and_oracle_length_rules() {
    let entries = vec![ManifestEntry {
        name: NHD.into(),
        length: 123,
        sha256: sha256_hex(b"abc"),
    }];
    let text = format_manifest("Option4BookChain.sims3", NOW, &entries);
    assert_eq!(parse_manifest(&text), entries);
    assert_eq!(parse_manifest(&text.replace('\n', "\r\n")), entries);
    assert!(parse_manifest("# comment\nbad\nhash -1 name\nhash +1 name\nhash 9223372036854775808 name\n").is_empty());
}

#[test]
fn keep_writes_manifest_and_replaces_the_copy() {
    let temp = Temp::new();
    let folder = temp.save();
    let mut files = fake_save(7);
    files.push((MANIFEST_FILE_NAME.into(), b"stale manifest".to_vec()));
    write_save(&folder, &files);
    let kept = keep(&folder, &files, NOW).unwrap();
    assert_eq!(kept, folder_for(&folder));
    assert_eq!(folder_for(&folder.join("")), kept);
    assert_eq!(verify(&kept), None);
    let entries = parse_manifest(&fs::read_to_string(kept.join(MANIFEST_FILE_NAME)).unwrap());
    assert_eq!(entries.len(), 4);
    assert!(entries.iter().any(|e| e.name == NHD && e.sha256.len() == 64));
    let later = fake_save(9);
    write_save(&folder, &later);
    keep(&folder, &later, NOW).unwrap();
    for (name, data) in later {
        assert_eq!(fs::read(kept.join(name)).unwrap(), data);
    }
    assert_eq!(verify(&kept), None);
    assert!(!PathBuf::from(format!("{}.incoming", kept.display())).exists());
    assert!(!PathBuf::from(format!("{}.old", kept.display())).exists());
    assert!(!kept.join("payload.bin").exists());
    fs::write(kept.join("TravelDB.package"), fake_dbpf(3, 256, 1)).unwrap();
    assert_eq!(verify(&kept), Some("TravelDB.package does not match its manifest".into()));
}

#[test]
fn keep_refuses_partial_and_mismatched_source_without_replacing_previous() {
    let temp = Temp::new();
    let folder = temp.save();
    let files = fake_save(7);
    write_save(&folder, &files);
    let kept = keep(&folder, &files, NOW).unwrap();
    let mut cut = files.clone();
    cut[2].1.truncate(2000);
    write_save(&folder, &cut);
    assert!(keep(&folder, &cut, NOW).unwrap_err().to_string().contains("not whole"));
    assert!(keep(&folder, &files, NOW).unwrap_err().to_string().contains("read back as"));
    assert_eq!(verify(&kept), None);
    assert_eq!(fs::read(kept.join(NHD)).unwrap(), files[2].1);
}

#[test]
fn shutdown_mid_copy_and_between_renames_preserves_the_previous_copy() {
    let temp = Temp::new();
    let folder = temp.save();
    let files = fake_save(7);
    write_save(&folder, &files);
    let kept = keep(&folder, &files, NOW).unwrap();
    // Deterministic crash image: staging exists but only one file reached disk.
    let incoming = PathBuf::from(format!("{}.incoming", kept.display()));
    fs::create_dir(&incoming).unwrap();
    fs::copy(folder.join(NHD), incoming.join(NHD)).unwrap();
    assert_eq!(verify(&kept), None);
    assert!(!ensure_bootable(&folder, COMPACT).restored);
    keep(&folder, &files, NOW).unwrap();
    assert!(!incoming.exists());
    let old = PathBuf::from(format!("{}.old", kept.display()));
    fs::rename(&kept, &old).unwrap();
    fs::remove_dir_all(&folder).unwrap();
    assert!(ensure_bootable(&folder, COMPACT).restored);
    assert_eq!(verify(&kept), None);
}

#[test]
fn keep_failure_is_nonfatal_to_the_successful_game_save() {
    let temp = Temp::new();
    let folder = temp.save();
    let files = fake_save(7);
    write_save(&folder, &files);
    let kept = keep(&folder, &files, NOW).unwrap();
    // Snapshot is whole but its source vanished before the background copy.
    fs::remove_file(folder.join(NHD)).unwrap();
    keep_in_background(folder.clone(), files, NOW.into()).join().unwrap();
    assert_eq!(verify(&kept), None);
    assert_eq!(fs::read(folder.join("Meta.data")).unwrap(), fake_dbpf(1, 64, 7));
}

#[test]
fn boot_restores_missing_or_cut_save_and_leaves_newer_whole_save() {
    let temp = Temp::new();
    let folder = temp.save();
    let files = fake_save(7);
    write_save(&folder, &files);
    let kept = keep(&folder, &files, NOW).unwrap();
    let same = ensure_bootable(&folder, COMPACT);
    assert!(!same.restored);
    assert_eq!(same.detail, "whole, the same as the last good copy");
    fs::write(folder.join(NHD), &files[2].1[..2000]).unwrap();
    fs::remove_file(folder.join("TravelDB.package")).unwrap();
    let cut = ensure_bootable(&folder, COMPACT);
    let aside = PathBuf::from(format!("{}.broken-{COMPACT}", folder.display()));
    assert!(cut.restored, "{}", cut.detail);
    assert!(cut.detail.contains("broken-20261021-230500"));
    assert_eq!(fs::read_dir(aside).unwrap().count(), 3);
    assert!(!folder.join(MANIFEST_FILE_NAME).exists());
    assert_eq!(verify(&kept), None);
    for (name, data) in &files {
        assert_eq!(fs::read(folder.join(name)).unwrap(), *data);
    }
    fs::remove_dir_all(&folder).unwrap();
    assert!(ensure_bootable(&folder, "20261021-230600").restored);
    write_save(&folder, &fake_save(9));
    let newer = ensure_bootable(&folder, COMPACT);
    assert!(!newer.restored);
    assert!(newer.detail.starts_with("whole, not the same"));
    assert_eq!(fs::read(folder.join("Meta.data")).unwrap()[0x60], 9);
    fs::remove_dir_all(kept).unwrap();
    let backup = PathBuf::from(format!("{}.backup", folder.display()));
    write_save(&backup, &files);
    fs::remove_file(folder.join(NHD)).unwrap();
    let no_copy = ensure_bootable(&folder, COMPACT);
    assert!(!no_copy.restored);
    assert!(no_copy.detail.starts_with("NOT WHOLE (no .nhd file) and no last good copy"));
    assert!(no_copy.detail.contains("Option4BookChain.sims3.backup is whole"));
}

#[test]
fn verify_reports_absent_empty_missing_mismatched_and_extra_files() {
    let temp = Temp::new();
    let folder = temp.save();
    assert_eq!(verify(&folder), Some("no manifest".into()));
    fs::create_dir(&folder).unwrap();
    fs::write(folder.join(MANIFEST_FILE_NAME), "# empty").unwrap();
    assert_eq!(verify(&folder), Some("empty manifest".into()));
    let files = fake_save(7);
    write_save(&folder, &files);
    let kept = keep(&folder, &files, NOW).unwrap();
    fs::write(kept.join("extra"), b"x").unwrap();
    assert_eq!(verify(&kept), Some("1 file(s) not in the manifest".into()));
    fs::remove_file(kept.join("extra")).unwrap();
    fs::remove_file(kept.join(NHD)).unwrap();
    assert_eq!(verify(&kept), Some(format!("{NHD} missing")));
    write_save(&kept, &fake_save(9));
    assert!(verify(&kept).unwrap().contains("does not match its manifest"));
    let whole_without_copy = ensure_bootable(&folder, COMPACT);
    assert!(!whole_without_copy.restored);
    assert!(whole_without_copy.detail.starts_with("whole (last good copy:"));
}

#[test]
fn kernel_driver_walks_all_phases_using_copied_source_bytes() {
    let temp = Temp::new();
    let payload = kernel_dbpf();
    let files = vec![(NHD.into(), payload.clone())];
    write_save(&temp.save(), &files);
    let (phases, manifest) = drive_committer_phases(&temp.0, "Option4BookChain", &payload).unwrap();
    assert_eq!(phases, vec![
        CommitPhase::StagingTmp,
        CommitPhase::VerifyNhdHeaderAndChecksums,
        CommitPhase::BackupRotate,
        CommitPhase::AtomicRenameCommit,
        CommitPhase::Done,
    ]);
    assert_eq!(manifest.count, 1);
    assert_eq!(manifest.tile_sum, manifest.idx_start - 0x60);
    assert_eq!(fs::read(temp.save().join(NHD)).unwrap(), payload);
    assert_eq!(verify(&folder_for(&temp.save())), None);
    assert_eq!(fs::read(folder_for(&temp.save()).join(NHD)).unwrap(), payload);
}

#[test]
fn kernel_verification_fault_rolls_back_and_preserves_previous_copy() {
    let temp = Temp::new();
    let payload = kernel_dbpf();
    write_save(&temp.save(), &[(NHD.into(), payload.clone())]);
    drive_committer_phases(&temp.0, "Option4BookChain", &payload).unwrap();
    let mut invalid = payload.clone();
    invalid[0x68 + 4 + 16..0x68 + 4 + 20].copy_from_slice(&0x64u32.to_le_bytes());
    write_save(&temp.save(), &[(NHD.into(), invalid.clone())]);
    assert_eq!(
        drive_committer_phases(&temp.0, "Option4BookChain", &invalid),
        Err((CommitPhase::VerifyNhdHeaderAndChecksums, CommitFault::VerifyRecordCrcMismatch))
    );
    assert_eq!(verify(&folder_for(&temp.save())), None);
    assert_eq!(fs::read(folder_for(&temp.save()).join(NHD)).unwrap(), payload);
    assert_eq!(
        drive_committer_phases(&temp.0, "Option4BookChain", b"not on disk"),
        Err((CommitPhase::StagingTmp, CommitFault::StageIoError))
    );
}

#[test]
fn kernel_rename_fault_restores_target_backup_and_lastgood() {
    let temp = Temp::new();
    let paths = SaveFileSet::for_name(&temp.0, "rollback");
    for (folder, bytes) in [
        (&paths.target, b"TARGET".as_slice()),
        (&paths.backup, b"BACKUP".as_slice()),
        (&paths.lastgood, b"LASTGOOD".as_slice()),
    ] {
        fs::create_dir(folder).unwrap();
        fs::write(folder.join("fixture"), bytes).unwrap();
    }
    let source = temp.0.join("source.nhd");
    fs::write(&source, kernel_dbpf()).unwrap();
    let mut committer = AtomicSaveCommitter::new(paths.clone());
    committer.stage(&[]).unwrap();
    fs::copy(source, paths.tmp.join("payload.bin")).unwrap();
    committer.verify_nhd().unwrap();
    committer.rotate_backup().unwrap();
    fs::create_dir(&paths.target).unwrap();
    assert_eq!(committer.atomic_rename_commit(), Err(CommitFault::RenameFault));
    assert_eq!(committer.phase, CommitPhase::StagingTmp);
    assert_eq!(fs::read(paths.target.join("fixture")).unwrap(), b"TARGET");
    assert_eq!(fs::read(paths.backup.join("fixture")).unwrap(), b"BACKUP");
    assert_eq!(fs::read(paths.lastgood.join("fixture")).unwrap(), b"LASTGOOD");
}
