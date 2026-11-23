#![forbid(unsafe_code)]

#[path = "../src/checkpoint.rs"]
mod checkpoint;

use checkpoint::*;
use std::cell::Cell;
use std::collections::BTreeSet;
use std::fs::{self, File, FileTimes};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, SystemTime};

static NEXT_TREE: AtomicU64 = AtomicU64::new(0);

struct SavesTree(PathBuf);

impl SavesTree {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "card59-checkpoint-{}-{}",
            std::process::id(),
            NEXT_TREE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }

    fn nhd(&self, folder: &Path, name: &str, seconds: u64) {
        fs::create_dir_all(folder).unwrap();
        let file = File::create(folder.join(name)).unwrap();
        file.set_times(
            FileTimes::new()
                .set_modified(SystemTime::UNIX_EPOCH + Duration::from_secs(seconds)),
        )
        .unwrap();
    }

    fn faults(&self) -> FaultInjector {
        FaultInjector {
            throw_on_next_save_game: false,
            run_dir: self.0.clone(),
        }
    }
}

impl Drop for SavesTree {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.0).unwrap();
    }
}

#[test]
fn checkpoint_names_and_boundary_constants() {
    assert_eq!(DEFAULT_SAVE_NAME, "SimsOnlineModTempWorld.sims3");
    assert_eq!(CHECKPOINT_READY_WAIT, Duration::from_secs(20));
    assert_eq!(CHECKPOINT_RETRY_DELAY, Duration::from_millis(500));
    assert_eq!(ensure_sims3_extension("Town"), "Town.sims3");
    assert_eq!(ensure_sims3_extension("Town.SiMs3"), "Town.SiMs3");
    for slot in [None, Some(""), Some(" \t")] {
        assert_eq!(checkpoint_save_id(slot, "Boot.sims3"), "Boot");
    }
    for slot in ["Join", "Join.sims3", "Join.SIMS3", "saves/Join.sims3", r"saves\Join.sims3"] {
        assert_eq!(checkpoint_save_id(Some(slot), "Boot.sims3"), "Join");
    }
    assert_eq!(checkpoint_save_id(None, "saves/Boot.sims3"), "Boot");
    assert_eq!(checkpoint_save_id(Some("Town.v2"), "Boot"), "Town.v2");
}

#[test]
fn reuse_is_enabled_unless_exactly_zero() {
    for value in [None, Some("1"), Some(""), Some("false"), Some(" 0"), Some("0 ")] {
        assert!(join_reuse_enabled_from(value));
    }
    assert!(!join_reuse_enabled_from(Some("0")));
    assert_eq!(
        join_reuse_enabled(),
        std::env::var("SIMSONLINE_JOIN_REUSE").ok().as_deref() != Some("0")
    );
}

#[test]
fn roots_include_configured_profile_and_redirected_documents_in_order() {
    let tree = SavesTree::new();
    let configured = tree.0.join("server-saves");
    let profile = tree.0.join("profile");
    let documents = tree.0.join("OneDrive").join("Documents");
    let relative = Path::new("Electronic Arts").join("The Sims 3").join("Saves");
    let expected = vec![
        configured.clone(),
        profile.join("Documents").join(&relative),
        documents.join(&relative),
    ];
    let roots = save_roots_with(Some(&configured), Some(&profile), Some(&documents));
    assert_eq!(roots, expected);
    for configured in [None, Some(Path::new("")), Some(Path::new(" \t"))] {
        assert_eq!(
            save_roots_with(configured, Some(&profile), Some(&documents)),
            expected[1..]
        );
    }
    assert!(save_roots_with(None, None, None).is_empty());
    for root in &roots {
        tree.nhd(&root.join("Town.sims3"), "Town.nhd", 1_700_000_000);
    }
    assert_eq!(
        find_newest_save_folder_in_roots(&roots, "Town"),
        Some(configured.join("Town.sims3"))
    );
    assert_eq!(
        find_newest_save_folder_in_roots(&roots[1..], "Town"),
        Some(profile.join("Documents").join(relative).join("Town.sims3"))
    );
}

#[test]
fn newest_nhd_wins_across_roots_and_both_folder_forms() {
    let tree = SavesTree::new();
    let roots = save_roots_with(
        Some(&tree.0.join("configured")),
        Some(&tree.0.join("profile")),
        Some(&tree.0.join("OneDrive").join("Documents")),
    );
    let older = roots[0].join("Town.sims3");
    let extensionless = roots[1].join("Town");
    let redirected = roots[2].join("Town.sims3");
    tree.nhd(&older, "Town.nhd", 1_700_000_000);
    tree.nhd(&extensionless, "Town.nhd", 1_700_000_100);
    tree.nhd(&redirected, "Town.nhd", 1_700_000_050);
    for input in ["Town", "Town.sims3"] {
        assert_eq!(
            find_newest_save_folder_in_roots(&roots, input),
            Some(extensionless.clone())
        );
    }
    // The maximum timestamp of every flat .nhd file counts, not just the first.
    tree.nhd(&redirected, "Travel.NHD", 1_700_000_200);
    assert_eq!(
        find_newest_save_folder_in_roots(&roots, "Town.sims3"),
        Some(redirected.clone())
    );
    tree.nhd(&older, "Town.nhd", 1_700_000_200);
    assert_eq!(
        find_newest_save_folder_in_roots(&roots, "Town"),
        Some(older.clone())
    );
    tree.nhd(&roots[0].join("Town"), "Town.nhd", 1_700_000_200);
    assert_eq!(
        find_newest_save_folder_in_roots(&roots, "Town"),
        Some(older)
    );
}

#[test]
fn folder_lookup_requires_flat_nhd_files_and_ignores_other_timestamps() {
    let tree = SavesTree::new();
    let root = tree.0.join("saves");
    let folder = root.join("Town.sims3");
    fs::create_dir_all(folder.join("not-a-file.nhd")).unwrap();
    tree.nhd(&folder.join("nested"), "Town.nhd", 1_700_000_200);
    tree.nhd(&folder, "Meta.data", 1_700_000_300);
    assert_eq!(
        find_newest_save_folder_in_roots(std::slice::from_ref(&root), "Town"),
        None
    );
    tree.nhd(&folder, "Town.nhd", 1_700_000_000);
    let sibling = root.join("Town");
    tree.nhd(&sibling, "Town.nhd", 1_700_000_100);
    assert_eq!(
        find_newest_save_folder_in_roots(std::slice::from_ref(&root), "Town"),
        Some(sibling)
    );
    assert_eq!(find_newest_save_folder_in_roots(&[root], "Missing"), None);
}

#[test]
fn environment_folder_lookup_uses_the_same_roots_without_mutating_environment() {
    let roots = save_roots();
    if let Some(profile) = std::env::var_os("USERPROFILE").or_else(|| std::env::var_os("HOME")) {
        assert!(roots.contains(
            &PathBuf::from(profile)
                .join("Documents")
                .join("Electronic Arts")
                .join("The Sims 3")
                .join("Saves")
        ));
    }
    let name = format!("card59-nonexistent-{}", std::process::id());
    assert_eq!(
        find_newest_save_folder(&name),
        find_newest_save_folder_in_roots(&roots, &name)
    );
}

#[test]
fn readiness_recognizes_only_the_four_oracle_refusals() {
    for reason in [
        "SAVE_GAME requires an in-world session",
        "Cannot save: CanSaveGame=false",
        "CANSAVEGAME=FALSE",
        "SaveGameInProgress=True",
        "savegameinprogress=true",
        "SAVE_GAME requires live mode",
    ] {
        assert!(is_not_ready_to_save(reason), "{reason}");
    }
    for reason in [
        "",
        "CanSaveGame=true",
        "SaveGameInProgress=False",
        "save_game requires live mode",
        "Requires an in-world session",
        "LoadSaveManager.SaveGame error code 16",
        "TS3W exited during SAVE_GAME",
    ] {
        assert!(!is_not_ready_to_save(reason), "{reason}");
    }
}

#[test]
fn scope_is_sorted_unique_decimal_u64s_or_dash() {
    assert_eq!(format_sim_scope(&BTreeSet::new()), "-");
    assert_eq!(
        format_sim_scope(&BTreeSet::from([u64::MAX, 7, 2, 7])),
        "2,7,18446744073709551615"
    );
}

#[test]
fn fault_sources_are_one_shot_even_when_both_are_armed() {
    let tree = SavesTree::new();
    let run_dir = tree.0.join(".omo").join("run");
    fs::create_dir_all(&run_dir).unwrap();
    let mut faults = FaultInjector {
        throw_on_next_save_game: false,
        run_dir: run_dir.clone(),
    };
    let flag = run_dir.join(DEV204_SAVE_FAILURE_FLAG);
    for (memory, file) in [(true, false), (false, true), (true, true)] {
        faults.throw_on_next_save_game = memory;
        if file {
            fs::write(&flag, []).unwrap();
        }
        assert_eq!(
            faults.check_and_consume_save_failure().unwrap_err(),
            "DEV-204 injected SAVE_GAME failure."
        );
        assert!(!faults.throw_on_next_save_game);
        assert!(!flag.exists());
        assert!(faults.check_and_consume_save_failure().is_ok());
    }
    fs::write(&flag, []).unwrap();
    assert!(faults.consume_dev204_flag(DEV204_SAVE_FAILURE_FLAG));
    assert!(!faults.consume_dev204_flag(DEV204_SAVE_FAILURE_FLAG));
    assert_eq!(FaultInjector::default().run_dir, Path::new(".omo").join("run"));
}

#[test]
fn fault_precedes_reuse_and_disabled_reuse_never_scans() {
    let tree = SavesTree::new();
    let gate = SaveGate::new();
    let mut faults = tree.faults();
    for enabled in [true, false] {
        fs::write(tree.0.join(DEV204_SAVE_FAILURE_FLAG), []).unwrap();
        let result = gate.checkpoint::<u64>(
            &mut faults,
            enabled,
            || panic!("fault must fail the join before its fingerprint scan"),
            || panic!("fault must fail the join before SAVE_GAME"),
        );
        assert_eq!(result.unwrap_err(), "DEV-204 injected SAVE_GAME failure.");
        assert_eq!(
            gate.checkpoint(
                &mut faults,
                false,
                || panic!("disabled reuse must not scan"),
                || Ok(7)
            ),
            Ok(7)
        );
    }
    let saves = Cell::new(0);
    let keeps = Cell::new(0);
    let cached = Cell::new(None);
    for _ in 0..2 {
        assert_eq!(
            gate.checkpoint(
                &mut faults,
                true,
                || cached.get(),
                || {
                    saves.set(saves.get() + 1);
                    keeps.set(keeps.get() + 1);
                    cached.set(Some(9));
                    Ok(9)
                },
            ),
            Ok(9)
        );
    }
    assert_eq!(saves.get(), 1);
    assert_eq!(keeps.get(), 1, "Guard A must not keep a reused world");
    assert!(gate.lock().is_ok());
}

#[test]
fn failed_save_releases_the_gate_and_preserves_the_named_refusal() {
    let tree = SavesTree::new();
    let gate = SaveGate::default();
    let mut faults = tree.faults();
    assert_eq!(
        gate.checkpoint::<u64>(
            &mut faults,
            false,
            || panic!("disabled"),
            || Err("TS3W exited during SAVE_GAME".to_owned())
        ),
        Err("TS3W exited during SAVE_GAME".to_owned())
    );
    assert_eq!(
        gate.checkpoint(&mut faults, false, || None, || Ok(11)),
        Ok(11)
    );
}

#[test]
fn concurrent_joins_scan_after_the_previous_save_and_share_it() {
    let tree = SavesTree::new();
    let gate = Arc::new(SaveGate::new());
    let cache = Arc::new(Mutex::new(None));
    let actions = Arc::new(Mutex::new(Vec::new()));
    let (inside_tx, inside_rx) = mpsc::channel();
    let (finish_tx, finish_rx) = mpsc::channel();
    let (attempt_tx, attempt_rx) = mpsc::channel();
    let (done_tx, done_rx) = mpsc::channel();
    let timeout = Duration::from_secs(10);
    std::thread::scope(|scope| {
        let first_gate = Arc::clone(&gate);
        let first_cache = Arc::clone(&cache);
        let first_actions = Arc::clone(&actions);
        let first_done = done_tx.clone();
        let mut first_faults = tree.faults();
        let first = scope.spawn(move || {
            let result = first_gate.checkpoint(
                &mut first_faults,
                true,
                || {
                    first_actions.lock().unwrap().push("scan");
                    *first_cache.lock().unwrap()
                },
                || {
                    first_actions.lock().unwrap().push("save");
                    inside_tx.send(()).unwrap();
                    finish_rx.recv_timeout(timeout).unwrap();
                    *first_cache.lock().unwrap() = Some(42);
                    first_actions.lock().unwrap().push("keep");
                    Ok(42)
                },
            );
            first_done.send(()).unwrap();
            result
        });
        inside_rx.recv_timeout(timeout).unwrap();
        let second_gate = Arc::clone(&gate);
        let second_cache = Arc::clone(&cache);
        let second_actions = Arc::clone(&actions);
        let mut second_faults = tree.faults();
        let second = scope.spawn(move || {
            attempt_tx.send(()).unwrap();
            let result = second_gate.checkpoint(
                &mut second_faults,
                true,
                || {
                    second_actions.lock().unwrap().push("scan");
                    *second_cache.lock().unwrap()
                },
                || panic!("the second join must reuse the completed first save"),
            );
            done_tx.send(()).unwrap();
            result
        });
        attempt_rx.recv_timeout(timeout).unwrap();
        finish_tx.send(()).unwrap();
        for _ in 0..2 {
            done_rx.recv_timeout(timeout).unwrap();
        }
        assert_eq!(first.join().unwrap(), Ok(42));
        assert_eq!(second.join().unwrap(), Ok(42));
    });
    assert_eq!(*actions.lock().unwrap(), ["scan", "save", "keep", "scan"]);
}
