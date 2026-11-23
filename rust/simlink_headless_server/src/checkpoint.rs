#![forbid(unsafe_code)]

//! Card 59 section B: checkpoint policy around the existing SAVE_GAME boundary.
//! TS3 writes the save; this module only selects folders and orders operations.

use std::collections::BTreeSet;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{LockResult, Mutex, MutexGuard};
use std::time::{Duration, SystemTime};

pub const DEFAULT_SAVE_NAME: &str = "SimsOnlineModTempWorld.sims3";
pub const CHECKPOINT_READY_WAIT: Duration = Duration::from_secs(20);
pub const CHECKPOINT_RETRY_DELAY: Duration = Duration::from_millis(500);
pub const DEV204_SAVE_FAILURE_FLAG: &str = "dev204-inject-save-failure.flag";

pub fn join_reuse_enabled() -> bool {
    join_reuse_enabled_from(env::var("SIMSONLINE_JOIN_REUSE").ok().as_deref())
}

pub fn join_reuse_enabled_from(value: Option<&str>) -> bool {
    value != Some("0")
}

pub fn ensure_sims3_extension(save_name: &str) -> String {
    if save_name.to_ascii_lowercase().ends_with(".sims3") {
        save_name.to_owned()
    } else {
        format!("{save_name}.sims3")
    }
}

pub fn checkpoint_save_id(checkpoint_save: Option<&str>, boot_save: &str) -> String {
    let selected = checkpoint_save
        .filter(|name| !name.trim().is_empty())
        .unwrap_or(boot_save);
    // Accept either path separator even when a Windows save name is tested elsewhere.
    let name = selected.rsplit(['/', '\\']).next().unwrap_or(selected);
    if name.to_ascii_lowercase().ends_with(".sims3") {
        name[..name.len() - 6].to_owned()
    } else {
        name.to_owned()
    }
}

pub fn save_roots_with(
    configured_saves_dir: Option<&Path>,
    user_profile: Option<&Path>,
    my_documents: Option<&Path>,
) -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(configured) =
        configured_saves_dir.filter(|path| !path.as_os_str().to_string_lossy().trim().is_empty())
    {
        roots.push(configured.to_path_buf());
    }
    let sims3 = Path::new("Electronic Arts").join("The Sims 3").join("Saves");
    if let Some(profile) = user_profile {
        roots.push(profile.join("Documents").join(&sims3));
    }
    if let Some(documents) = my_documents {
        roots.push(documents.join(sims3));
    }
    roots
}

pub fn save_roots() -> Vec<PathBuf> {
    let configured = env::var_os("SIMSONLINE_SERVER_SAVES_DIR").map(PathBuf::from);
    let profile = env::var_os("USERPROFILE")
        .or_else(|| env::var_os("HOME"))
        .map(PathBuf::from);
    let documents = shell_documents();
    save_roots_with(
        configured.as_deref(),
        profile.as_deref(),
        documents.as_deref(),
    )
}

#[cfg(windows)]
fn shell_documents() -> Option<PathBuf> {
    // .NET's shell lookup honours Known Folder redirection (including OneDrive).
    // Use the same lookup without adding unsafe Windows FFI to the daemon.
    let output = std::process::Command::new("powershell.exe")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); [Environment]::GetFolderPath('MyDocuments')",
        ])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let path = String::from_utf8(output.stdout).ok()?;
    let path = path.trim_end_matches(['\r', '\n']);
    (!path.is_empty()).then(|| PathBuf::from(path))
}

#[cfg(not(windows))]
fn shell_documents() -> Option<PathBuf> {
    env::var_os("HOME").map(|home| PathBuf::from(home).join("Documents"))
}

pub fn find_newest_save_folder(save_name: &str) -> Option<PathBuf> {
    find_newest_save_folder_in_roots(&save_roots(), save_name)
}

pub fn find_newest_save_folder_in_roots(
    roots: &[PathBuf],
    save_name: &str,
) -> Option<PathBuf> {
    let folder_name = ensure_sims3_extension(save_name);
    let save_id = &folder_name[..folder_name.len() - 6];
    let mut newest: Option<(SystemTime, PathBuf)> = None;
    for root in roots {
        // The suffix form wins an exact tie within a root. Root order wins
        // across roots, just as the oracle's stable descending ordering does.
        for candidate in [root.join(&folder_name), root.join(save_id)] {
            let Some(modified) = newest_nhd_timestamp(&candidate) else {
                continue;
            };
            if newest.as_ref().is_none_or(|(at, _)| modified > *at) {
                newest = Some((modified, candidate));
            }
        }
    }
    newest.map(|(_, path)| path)
}

fn newest_nhd_timestamp(folder: &Path) -> Option<SystemTime> {
    fs::read_dir(folder)
        .ok()?
        .filter_map(Result::ok)
        .filter(|entry| {
            entry
                .path()
                .extension()
                .is_some_and(|extension| extension.eq_ignore_ascii_case("nhd"))
        })
        .filter_map(|entry| {
            let metadata = entry.metadata().ok()?;
            metadata.is_file().then(|| metadata.modified().ok()).flatten()
        })
        .max()
}

pub fn is_not_ready_to_save(reason: &str) -> bool {
    let folded = reason.to_ascii_lowercase();
    reason.contains("requires an in-world session")
        || folded.contains("cansavegame=false")
        || folded.contains("savegameinprogress=true")
        || reason.contains("SAVE_GAME requires live mode")
}

pub fn format_sim_scope(claimed_this_run: &BTreeSet<u64>) -> String {
    if claimed_this_run.is_empty() {
        "-".to_owned()
    } else {
        claimed_this_run
            .iter()
            .map(u64::to_string)
            .collect::<Vec<_>>()
            .join(",")
    }
}

pub struct FaultInjector {
    pub throw_on_next_save_game: bool,
    pub run_dir: PathBuf,
}

impl Default for FaultInjector {
    fn default() -> Self {
        Self {
            throw_on_next_save_game: false,
            run_dir: PathBuf::from(".omo").join("run"),
        }
    }
}

impl FaultInjector {
    pub fn consume_dev204_flag(&self, name: &str) -> bool {
        // Successful deletion consumes the trigger atomically; a missing flag
        // or a failed deletion is not a newly injected failure.
        fs::remove_file(self.run_dir.join(name)).is_ok()
    }

    pub fn check_and_consume_save_failure(&mut self) -> Result<(), String> {
        // Consume both sources even when the in-memory trigger is also armed:
        // section B.6 must not leave a file armed for a later save.
        let flag = self.consume_dev204_flag(DEV204_SAVE_FAILURE_FLAG);
        let armed = std::mem::take(&mut self.throw_on_next_save_game);
        if armed || flag {
            Err("DEV-204 injected SAVE_GAME failure.".to_owned())
        } else {
            Ok(())
        }
    }
}

/// One gate shared by public saves and join checkpoints.
#[derive(Default)]
pub struct SaveGate(Mutex<()>);

impl SaveGate {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn lock(&self) -> LockResult<MutexGuard<'_, ()>> {
        self.0.lock()
    }

    /// Scan only after the previous save has completed. The fresh callback owns
    /// SAVE_GAME, reading its files, cache publication and scheduling last-good.
    /// Guard A: a reuse HIT never invokes that callback, so it never keeps again.
    pub fn checkpoint<T>(
        &self,
        faults: &mut FaultInjector,
        reuse_enabled: bool,
        try_reuse: impl FnOnce() -> Option<T>,
        fresh: impl FnOnce() -> Result<T, String>,
    ) -> Result<T, String> {
        let _guard = self
            .lock()
            .map_err(|_| "SAVE_GAME checkpoint gate poisoned.".to_owned())?;
        faults.check_and_consume_save_failure()?;
        if reuse_enabled {
            if let Some(world) = try_reuse() {
                return Ok(world);
            }
        }
        fresh()
    }
}
