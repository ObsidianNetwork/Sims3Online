#![forbid(unsafe_code)]
//! Card 59: copy-only last-good saves. TS3, never this module, authors save content.
use sim96_kernel_cleanroom::persistence::{
    AtomicSaveCommitter, CommitFault, CommitPhase, DbpfManifest, SaveFileSet,
};
use std::fmt::Write;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::thread::{self, JoinHandle};

pub const SUFFIX: &str = ".lastgood";
pub const MANIFEST_FILE_NAME: &str = "simsonline-lastgood.txt";
pub const WORLD_MARKER_FILE_NAME: &str = "simsonline-world.txt";
static KEEP_GATE: Mutex<()> = Mutex::new(());

/// SHA-256's standard 32-bit compression and big-endian padding, without crates.
pub fn sha256_hex(data: &[u8]) -> String {
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];
    let mut state = [
        0x6a09e667u32, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];
    let block_count = (data.len() + 9).div_ceil(64);
    let bit_length = (data.len() as u64).wrapping_mul(8).to_be_bytes();
    for block_index in 0..block_count {
        let mut block = [0u8; 64];
        for (i, byte) in block.iter_mut().enumerate() {
            let offset = block_index * 64 + i;
            *byte = if offset < data.len() {
                data[offset]
            } else if offset == data.len() {
                0x80
            } else if offset >= block_count * 64 - 8 {
                bit_length[offset - (block_count * 64 - 8)]
            } else {
                0
            };
        }
        let mut words = [0u32; 64];
        for (word, bytes) in words.iter_mut().zip(block.as_chunks::<4>().0) {
            *word = u32::from_be_bytes(*bytes);
        }
        for i in 16..64 {
            let a = words[i - 15];
            let b = words[i - 2];
            let s0 = a.rotate_right(7) ^ a.rotate_right(18) ^ (a >> 3);
            let s1 = b.rotate_right(17) ^ b.rotate_right(19) ^ (b >> 10);
            words[i] = words[i - 16]
                .wrapping_add(s0)
                .wrapping_add(words[i - 7])
                .wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut h] = state;
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let choose = (e & f) ^ (!e & g);
            let t1 = h
                .wrapping_add(s1)
                .wrapping_add(choose)
                .wrapping_add(K[i])
                .wrapping_add(words[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let majority = (a & b) ^ (a & c) ^ (b & c);
            let t2 = s0.wrapping_add(majority);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(t1);
            d = c;
            c = b;
            b = a;
            a = t1.wrapping_add(t2);
        }
        for (word, value) in state.iter_mut().zip([a, b, c, d, e, f, g, h]) {
            *word = word.wrapping_add(value);
        }
    }
    let mut hex = String::with_capacity(64);
    for word in state {
        write!(hex, "{word:08x}").unwrap();
    }
    hex
}

fn append(path: &Path, suffix: &str) -> PathBuf {
    let mut name = path.as_os_str().to_os_string();
    name.push(suffix);
    PathBuf::from(name)
}

pub fn folder_for(save_folder: &Path) -> PathBuf {
    // Components drop a trailing directory separator without lossy Unicode conversion.
    append(&save_folder.components().collect::<PathBuf>(), SUFFIX)
}

pub fn dbpf_problem(data: &[u8]) -> Option<String> {
    if data.len() < 4 || &data[..4] != b"DBPF" {
        return None;
    }
    if data.len() < 0x60 {
        return Some(format!("DBPF header cut short ({} bytes)", data.len()));
    }
    let read = |offset| {
        u64::from(u32::from_le_bytes(
            data[offset..offset + 4].try_into().unwrap(),
        ))
    };
    let count = read(0x24);
    let size = read(0x2c);
    let end = read(0x40) + size;
    if end != data.len() as u64 {
        return Some(format!(
            "DBPF index ends at {end}, the file at {}",
            data.len()
        ));
    }
    (size != 4 + 32 * count)
        .then(|| format!("DBPF index size {size} does not fit {count} records"))
}

pub fn problem(files: &[(String, Vec<u8>)]) -> Option<String> {
    if !files
        .iter()
        .any(|(name, _)| name.to_ascii_lowercase().ends_with(".nhd"))
    {
        return Some("no .nhd file".into());
    }
    for (name, data) in files {
        if name == MANIFEST_FILE_NAME || name == WORLD_MARKER_FILE_NAME {
            continue;
        }
        if data.is_empty() {
            return Some(format!("{name} is empty"));
        }
        if let Some(reason) = dbpf_problem(data) {
            return Some(format!("{name}: {reason}"));
        }
    }
    None
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ManifestEntry {
    pub name: String,
    pub length: u64,
    pub sha256: String,
}

pub fn format_manifest(save_folder_name: &str, at_iso: &str, entries: &[ManifestEntry]) -> String {
    let mut text = format!(
        "# the last good copy of {save_folder_name}, kept {at_iso}. sha256 length name\n"
    );
    for entry in entries {
        writeln!(text, "{} {} {}", entry.sha256, entry.length, entry.name).unwrap();
    }
    text
}

pub fn parse_manifest(text: &str) -> Vec<ManifestEntry> {
    text.split('\n')
        .filter_map(|line| {
            let line = line.trim_end_matches('\r');
            if line.is_empty() || line.starts_with('#') {
                return None;
            }
            let mut parts = line.splitn(3, ' ');
            let hash = parts.next()?;
            let length = parts.next()?;
            if length.is_empty() || !length.bytes().all(|b| b.is_ascii_digit()) {
                return None;
            }
            let length: u64 = length.parse().ok()?;
            if length > i64::MAX as u64 {
                return None;
            }
            Some(ManifestEntry {
                name: parts.next()?.into(),
                length,
                sha256: hash.into(),
            })
        })
        .collect()
}

fn remove_tree(path: &Path) -> io::Result<()> {
    if path.is_dir() {
        fs::remove_dir_all(path)?;
    }
    Ok(())
}

fn read_files(folder: &Path) -> io::Result<Vec<(String, Vec<u8>)>> {
    let mut files = Vec::new();
    for entry in fs::read_dir(folder)? {
        let entry = entry?;
        if entry.file_type()?.is_file() {
            files.push((
                entry.file_name().to_string_lossy().into_owned(),
                fs::read(entry.path())?,
            ));
        }
    }
    Ok(files)
}

fn committer_for(save_folder: &Path) -> io::Result<AtomicSaveCommitter> {
    let target = folder_for(save_folder);
    let old = append(&target, ".old");
    // A process exit between the two directory renames leaves the previous copy here.
    if !target.exists() && old.is_dir() {
        fs::rename(&old, &target)?;
    }
    remove_tree(&old)?;
    let staging = append(&target, ".incoming");
    remove_tree(&staging)?;
    Ok(AtomicSaveCommitter::new(SaveFileSet {
        target,
        backup: old,
        lastgood: append(&folder_for(save_folder), ".older"),
        tmp: staging,
        bad: append(&folder_for(save_folder), ".bad"),
    }))
}

fn fault_error(fault: CommitFault) -> io::Error {
    io::Error::other(format!("last-good commit: {fault:?}"))
}

fn copy_staging(
    committer: &mut AtomicSaveCommitter,
    save_folder: &Path,
    files: &[(String, Vec<u8>)],
) -> io::Result<Vec<ManifestEntry>> {
    // Empty staging acquires the kernel's in-flight guard without authoring save bytes.
    committer.stage(&[]).map_err(fault_error)?;
    fs::remove_file(committer.fs.tmp.join("payload.bin"))?;
    let mut entries = Vec::new();
    for (name, data) in files {
        if name == MANIFEST_FILE_NAME {
            continue;
        }
        fs::copy(save_folder.join(name), committer.fs.tmp.join(name))?;
        let written = sha256_hex(&fs::read(committer.fs.tmp.join(name))?);
        let expected = sha256_hex(data);
        if written != expected {
            return Err(io::Error::other(format!(
                "{name} read back as {written}, written as {expected}"
            )));
        }
        entries.push(ManifestEntry {
            name: name.clone(),
            length: data.len() as u64,
            sha256: expected,
        });
    }
    Ok(entries)
}

/// Blocking copy primitive; the host calls `keep_in_background` off the game thread.
pub fn keep(
    save_folder: &Path,
    files: &[(String, Vec<u8>)],
    now_iso: &str,
) -> io::Result<PathBuf> {
    let _gate = KEEP_GATE.lock().map_err(|e| io::Error::other(e.to_string()))?;
    if let Some(reason) = problem(files) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("not kept: the save is not whole ({reason})"),
        ));
    }
    let mut committer = committer_for(save_folder)?;
    let result = (|| {
        let entries = copy_staging(&mut committer, save_folder, files)?;
        fs::write(
            committer.fs.tmp.join(MANIFEST_FILE_NAME),
            format_manifest(
                &save_folder.file_name().unwrap_or_default().to_string_lossy(),
                now_iso,
                &entries,
            ),
        )?;
        // The directory driver verifies *every* copied file above, using the C# oracle's
        // DBPF extent check plus SHA-256. The kernel's single-payload verifier is a
        // stricter card-11 model, not the controller's whole-save acceptance contract.
        committer.phase = CommitPhase::BackupRotate;
        committer.rotate_backup().map_err(fault_error)?;
        committer.atomic_rename_commit().map_err(fault_error)?;
        remove_tree(&committer.fs.backup)?;
        Ok(committer.fs.target.clone())
    })();
    if result.is_err() && committer.phase != CommitPhase::Done {
        committer
            .rollback_on_fault(CommitFault::StageIoError)
            .map_err(fault_error)?;
    }
    result
}

/// A failed backup is diagnostic only; the already successful game save stays successful.
pub fn keep_in_background(
    save_folder: PathBuf,
    files: Vec<(String, Vec<u8>)>,
    now_iso: String,
) -> JoinHandle<()> {
    thread::spawn(move || match keep(&save_folder, &files, &now_iso) {
        Ok(folder) => eprintln!("[save] last good copy kept: {}", folder.display()),
        Err(error) => eprintln!(
            "[save] LAST GOOD COPY NOT KEPT for {}: {error}",
            save_folder.display()
        ),
    })
}

pub fn verify(last_good_folder: &Path) -> Option<String> {
    let manifest = last_good_folder.join(MANIFEST_FILE_NAME);
    if !manifest.is_file() {
        return Some("no manifest".into());
    }
    let text = match fs::read_to_string(manifest) {
        Ok(text) => text,
        Err(error) => return Some(error.to_string()),
    };
    let entries = parse_manifest(&text);
    if entries.is_empty() {
        return Some("empty manifest".into());
    }
    for entry in &entries {
        let path = last_good_folder.join(&entry.name);
        if !path.is_file() {
            return Some(format!("{} missing", entry.name));
        }
        match fs::read(path) {
            Ok(data)
                if data.len() as u64 == entry.length && sha256_hex(&data) == entry.sha256 => {}
            Ok(_) => return Some(format!("{} does not match its manifest", entry.name)),
            Err(error) => return Some(error.to_string()),
        }
    }
    match read_files(last_good_folder) {
        Ok(files) => {
            let extra = files.len() as i64 - 1 - entries.len() as i64;
            (extra != 0).then(|| format!("{extra} file(s) not in the manifest"))
        }
        Err(error) => Some(error.to_string()),
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BootCheck {
    pub restored: bool,
    pub detail: String,
}

/// A failed restore is logged in the result and never prevents the host from trying load.
pub fn ensure_bootable(save_folder: &Path, now_compact: &str) -> BootCheck {
    let result = (|| -> io::Result<BootCheck> {
        let _gate = KEEP_GATE.lock().map_err(|e| io::Error::other(e.to_string()))?;
        let last_good = folder_for(save_folder);
        let old = append(&last_good, ".old");
        if !last_good.exists() && old.is_dir() {
            fs::rename(old, &last_good)?;
        }
        let files = if save_folder.is_dir() {
            Some(read_files(save_folder)?)
        } else {
            None
        };
        let save_problem = files
            .as_ref()
            .map_or_else(|| Some("missing".into()), |files| problem(files));
        let copy_problem = if last_good.is_dir() {
            verify(&last_good)
        } else {
            Some("none kept".into())
        };
        let Some(save_problem) = save_problem else {
            let detail = if let Some(reason) = copy_problem {
                format!("whole (last good copy: {reason})")
            } else {
                let entries = parse_manifest(&fs::read_to_string(
                    last_good.join(MANIFEST_FILE_NAME),
                )?);
                let files = files.as_ref().unwrap();
                let same = entries.len() == files.len()
                    && entries.iter().all(|entry| {
                        files.iter().any(|(name, data)| {
                            name == &entry.name
                                && data.len() as u64 == entry.length
                                && sha256_hex(data) == entry.sha256
                        })
                    });
                if same {
                    "whole, the same as the last good copy".into()
                } else {
                    "whole, not the same as the last good copy (a save the controller did not keep): left as it is".into()
                }
            };
            return Ok(BootCheck { restored: false, detail });
        };
        if let Some(copy_problem) = copy_problem {
            let backup = append(save_folder, ".backup");
            let backup_note = if backup.is_dir() && problem(&read_files(&backup)?).is_none() {
                format!(
                    "; {} is whole and can be put back by hand",
                    backup.file_name().unwrap_or_default().to_string_lossy()
                )
            } else {
                String::new()
            };
            return Ok(BootCheck {
                restored: false,
                detail: format!("NOT WHOLE ({save_problem}) and no last good copy to restore ({copy_problem}){backup_note}; loading it as it is"),
            });
        }
        let aside = files.map(|_| append(save_folder, &format!(".broken-{now_compact}")));
        if let Some(aside) = &aside {
            fs::rename(save_folder, aside)?;
        }
        let restoring = append(save_folder, ".restoring");
        remove_tree(&restoring)?;
        fs::create_dir(&restoring)?;
        for entry in fs::read_dir(&last_good)? {
            let entry = entry?;
            if entry.file_type()?.is_file() && entry.file_name() != MANIFEST_FILE_NAME {
                fs::copy(entry.path(), restoring.join(entry.file_name()))?;
            }
        }
        fs::rename(restoring, save_folder)?;
        let mut detail = format!(
            "NOT WHOLE ({save_problem}): restored from {}",
            last_good.file_name().unwrap_or_default().to_string_lossy()
        );
        if let Some(aside) = aside {
            write!(
                detail,
                ", the broken folder kept as {}",
                aside.file_name().unwrap_or_default().to_string_lossy()
            ).unwrap();
        }
        Ok(BootCheck { restored: true, detail })
    })();
    result.unwrap_or_else(|error| BootCheck {
        restored: false,
        detail: format!("last good check failed ({error}); loading it as it is"),
    })
}

/// Exercise the kernel's single-NHD verifier and all five phases without staging
/// caller-authored bytes: `dir/name.sims3` must already contain the matching NHD.
pub fn drive_committer_phases(
    dir: &Path,
    name: &str,
    nhd_payload: &[u8],
) -> Result<(Vec<CommitPhase>, DbpfManifest), (CommitPhase, CommitFault)> {
    let _gate = KEEP_GATE
        .lock()
        .map_err(|_| (CommitPhase::StagingTmp, CommitFault::Reentrant))?;
    let source = dir.join(if name.to_ascii_lowercase().ends_with(".sims3") {
        name.to_owned()
    } else {
        format!("{name}.sims3")
    });
    let files = read_files(&source)
        .map_err(|_| (CommitPhase::StagingTmp, CommitFault::StageIoError))?;
    let nhd_name = files.iter().find_map(|(name, data)| {
        (name.to_ascii_lowercase().ends_with(".nhd") && data == nhd_payload)
            .then_some(name.clone())
    }).ok_or((CommitPhase::StagingTmp, CommitFault::StageIoError))?;
    let mut committer = committer_for(&source)
        .map_err(|_| (CommitPhase::StagingTmp, CommitFault::StageIoError))?;
    let mut phases = vec![committer.phase];
    let result = (|| {
        let entries = copy_staging(&mut committer, &source, &files)
            .map_err(|_| (committer.phase, CommitFault::StageIoError))?;
        phases.push(committer.phase);
        fs::copy(source.join(nhd_name), committer.fs.tmp.join("payload.bin"))
            .map_err(|_| (committer.phase, CommitFault::StageIoError))?;
        let phase = committer.phase;
        let manifest = committer.verify_nhd().map_err(|fault| (phase, fault))?;
        phases.push(committer.phase);
        fs::remove_file(committer.fs.tmp.join("payload.bin"))
            .map_err(|_| (committer.phase, CommitFault::StageIoError))?;
        fs::write(
            committer.fs.tmp.join(MANIFEST_FILE_NAME),
            format_manifest(name, "kernel-phase-check", &entries),
        ).map_err(|_| (committer.phase, CommitFault::StageIoError))?;
        committer.rotate_backup().map_err(|fault| (committer.phase, fault))?;
        phases.push(committer.phase);
        let phase = committer.phase;
        committer.atomic_rename_commit().map_err(|fault| (phase, fault))?;
        phases.push(committer.phase);
        remove_tree(&committer.fs.backup)
            .map_err(|_| (committer.phase, CommitFault::BackupCopyFault))?;
        Ok((phases, manifest))
    })();
    if let Err((phase, fault)) = result {
        committer.rollback_on_fault(fault).map_err(|fault| (phase, fault))?;
        return Err((phase, fault));
    }
    result
}
