#![forbid(unsafe_code)]

use simlink_headless_server::{messages, wire_v1};
use std::cmp::Ordering;
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

#[path = "../src/world_save.rs"]
mod world_save;
use world_save::*;

struct TempDirectory(PathBuf);

impl TempDirectory {
    fn new() -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "card59-world-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, AtomicOrdering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.0).unwrap();
    }
}

fn fixture() -> WorldSave {
    WorldSave {
        save_name: "Join".into(),
        files: vec![
            WorldSaveFile {
                name: "empty.dat".into(),
                data: vec![],
            },
            WorldSaveFile {
                name: "exact.nhd".into(),
                data: (0..WORLD_CHUNK_RAW_BYTES).map(|i| i as u8).collect(),
            },
            WorldSaveFile {
                name: "over.package".into(),
                data: vec![0xab; WORLD_CHUNK_RAW_BYTES + 1],
            },
        ],
    }
}

#[test]
fn flat_files_use_ordinal_ignore_case_order_and_suffix() {
    let temp = TempDirectory::new();
    for name in ["_world.nhd", "zWorld.nhd", "Alpha.data"] {
        fs::write(temp.0.join(name), name.as_bytes()).unwrap();
    }
    fs::create_dir(temp.0.join("nested")).unwrap();
    fs::write(temp.0.join("nested/ignored.data"), b"nested").unwrap();
    let world = WorldSave::read_folder(&temp.0, "Join.SIMS3").unwrap();
    assert_eq!(world.folder_name(), "Join.SIMS3");
    assert_eq!(
        world.files.iter().map(|file| file.name.as_str()).collect::<Vec<_>>(),
        ["Alpha.data", "zWorld.nhd", "_world.nhd"]
    );
    for file in world.files {
        assert_eq!(file.data, file.name.as_bytes());
    }
    assert_eq!(ordinal_ignore_case_cmp("z", "Z"), Ordering::Equal);
    assert_eq!(ordinal_ignore_case_cmp("z", "_"), Ordering::Less);
    assert_eq!(ordinal_ignore_case_cmp("\u{e9}", "\u{c9}"), Ordering::Equal);
    assert_eq!(ordinal_ignore_case_cmp("\u{df}", "SS"), Ordering::Greater);
    assert_eq!(ordinal_ignore_case_cmp("\u{131}", "I"), Ordering::Greater);
    assert_eq!(ordinal_ignore_case_cmp("\u{10428}", "\u{10400}"), Ordering::Equal);
    // UTF-16 ordinals put the surrogate pair before a high BMP character.
    assert_eq!(ordinal_ignore_case_cmp("\u{10000}", "\u{e000}"), Ordering::Less);
}

#[test]
fn chunks_match_oracle_empty_exact_and_over_boundary_sequence() {
    let world = fixture();
    assert_eq!(WORLD_CHUNK_RAW_BYTES, 262_144);
    assert_eq!(WORLD_CHUNK_B64_BYTES, 349_528);
    assert_eq!(world.folder_name(), "Join.sims3");
    assert_eq!(world.total_bytes(), 524_289);
    let chunks = world.to_chunks(WORLD_CHUNK_RAW_BYTES);
    assert_eq!(
        chunks.iter().map(|c| (c.file_index, c.offset, c.data.len())).collect::<Vec<_>>(),
        [(0, 0, 0), (1, 0, 262_144), (2, 0, 262_144), (2, 262_144, 1)]
    );
    for (index, file) in world.files.iter().enumerate() {
        let mut bytes = Vec::new();
        for chunk in chunks.iter().filter(|c| c.file_index == index) {
            assert_eq!(chunk.folder_name, world.folder_name());
            assert_eq!(chunk.file_count, 3);
            assert_eq!(chunk.file_name, file.name);
            assert_eq!(chunk.file_length, file.data.len() as u64);
            bytes.extend_from_slice(&chunk.data);
        }
        assert_eq!(bytes, file.data);
    }
    let small = world.to_chunks(100_000);
    assert_eq!(small.iter().filter(|c| c.file_index == 1).count(), 3);
    assert!(WorldSave { save_name: "Empty".into(), files: vec![] }
        .to_chunks(WORLD_CHUNK_RAW_BYTES).is_empty());
}

#[test]
fn wire_has_one_six_field_event_and_world_sized_bulk_per_file() {
    let world = fixture();
    let lines = world.encode_world_file_lines(10, 55, 77, 100);
    assert_eq!(lines.len(), 7);
    let mut position = 0;
    for (file_index, file) in world.files.iter().enumerate() {
        let fields = lines[position].split('|').map(str::to_owned).collect::<Vec<_>>();
        let event = wire_v1::decode_event(&fields).unwrap();
        assert_eq!(event.seq, 10 + file_index as u64);
        assert_eq!(event.tick, 55);
        assert_eq!(event.corr, 77);
        assert_eq!(event.kind, "WorldFile");
        assert_eq!(event.fields, vec![
            messages::hex_encode("Join.sims3"), file_index.to_string(), "3".into(),
            messages::hex_encode(&file.name), file.data.len().to_string(),
            format!("b{}", 100 + file_index),
        ]);
        position += 1;
        let count = file.data.len().div_ceil(WORLD_CHUNK_RAW_BYTES).max(1);
        let mut bytes = Vec::new();
        for index in 0..count {
            let fields = lines[position].split('|').collect::<Vec<_>>();
            assert_eq!(&fields[..4], [
                "BULK", &(100 + file_index).to_string(), &index.to_string(), &count.to_string(),
            ]);
            let decoded = wire_v1::b64_decode(fields[4]).unwrap();
            assert_eq!(decoded.len(), WORLD_CHUNK_RAW_BYTES.min(file.data.len() - index * WORLD_CHUNK_RAW_BYTES));
            if decoded.len() == WORLD_CHUNK_RAW_BYTES {
                assert_eq!(fields[4].len(), WORLD_CHUNK_B64_BYTES);
            }
            bytes.extend(decoded);
            position += 1;
        }
        assert_eq!(bytes, file.data);
    }
    assert!(lines[1].ends_with("|0|1|"));
}

#[test]
fn writer_preserves_player_save_and_replaces_only_marked_folder() {
    let temp = TempDirectory::new();
    let world = fixture();
    let target = temp.0.join(world.folder_name());
    fs::create_dir(&target).unwrap();
    fs::write(target.join("player.data"), b"keep me").unwrap();
    let mut refused = WorldSaveWriter::new(temp.0.clone());
    let error = refused.add(&world.to_chunks(WORLD_CHUNK_RAW_BYTES)[0]).unwrap_err();
    assert!(error.to_string().contains("wasn't written by SimsOnline; refusing to replace it"));
    assert_eq!(fs::read(target.join("player.data")).unwrap(), b"keep me");
    assert!(!temp.0.join("Join.sims3.incoming").exists());
    assert!(!refused.is_complete());
    assert_eq!(refused.folder_name(), None);

    fs::write(target.join(MARKER_FILE_NAME), b"prior online world").unwrap();
    let mut writer = WorldSaveWriter::new(temp.0.clone());
    for chunk in world.to_chunks(WORLD_CHUNK_RAW_BYTES) {
        assert!(!writer.is_complete());
        writer.add(&chunk).unwrap();
    }
    assert!(writer.is_complete());
    assert_eq!(writer.folder_name(), Some("Join.sims3"));
    assert_eq!(writer.bytes_written(), world.total_bytes());
    assert!(!target.join("player.data").exists());
    assert!(target.join(MARKER_FILE_NAME).is_file());
    assert!(!temp.0.join("Join.sims3.incoming").exists());
    for file in world.files {
        assert_eq!(fs::read(target.join(file.name)).unwrap(), file.data);
    }
}

#[test]
fn writer_cleans_staging_on_bad_order_and_explicit_abort() {
    let temp = TempDirectory::new();
    let world = fixture();
    let chunks = world.to_chunks(WORLD_CHUNK_RAW_BYTES);
    let staging = temp.0.join("Join.sims3.incoming");
    let mut writer = WorldSaveWriter::new(temp.0.clone());
    writer.add(&chunks[0]).unwrap();
    assert!(staging.exists());
    assert!(writer.add(&chunks[2]).is_err());
    assert!(!staging.exists());
    assert!(!temp.0.join(world.folder_name()).exists());

    let mut writer = WorldSaveWriter::new(temp.0.clone());
    writer.add(&chunks[0]).unwrap();
    writer.abort();
    assert!(!staging.exists());
    let mut writer = WorldSaveWriter::new(temp.0.clone());
    writer.add(&chunks[0]).unwrap();
    drop(writer);
    assert!(!staging.exists());
}

#[test]
fn writer_rejects_invalid_names_lengths_and_changed_metadata() {
    let temp = TempDirectory::new();
    let chunks = fixture().to_chunks(WORLD_CHUNK_RAW_BYTES);
    let mut bad_chunks = Vec::new();
    for name in ["../escape.sims3", "Join", "C:\\escape.sims3"] {
        let mut chunk = chunks[0].clone();
        chunk.folder_name = name.into();
        bad_chunks.push(chunk);
    }
    for name in ["../escape", MARKER_FILE_NAME, ".", ""] {
        let mut chunk = chunks[0].clone();
        chunk.file_name = name.into();
        bad_chunks.push(chunk);
    }
    let mut over = chunks[0].clone();
    over.data.push(1);
    bad_chunks.push(over);
    let mut bad_count = chunks[0].clone();
    bad_count.file_count = 0;
    bad_chunks.push(bad_count);
    for chunk in bad_chunks {
        let mut writer = WorldSaveWriter::new(temp.0.clone());
        assert!(writer.add(&chunk).is_err(), "{chunk:?}");
        assert!(!temp.0.join("Join.sims3.incoming").exists());
    }
    for change in 0..4 {
        let mut writer = WorldSaveWriter::new(temp.0.clone());
        for chunk in &chunks[..3] {
            writer.add(chunk).unwrap();
        }
        let mut chunk = chunks[3].clone();
        match change {
            0 => chunk.offset = 0,
            1 => chunk.file_name = "other".into(),
            2 => chunk.file_length += 1,
            _ => chunk.file_count += 1,
        }
        assert!(writer.add(&chunk).is_err());
        assert!(!temp.0.join("Join.sims3.incoming").exists());
    }
}

#[test]
fn writer_rechecks_marker_at_commit() {
    let temp = TempDirectory::new();
    let world = fixture();
    let chunks = world.to_chunks(WORLD_CHUNK_RAW_BYTES);
    let mut writer = WorldSaveWriter::new(temp.0.clone());
    for chunk in &chunks[..3] {
        writer.add(chunk).unwrap();
    }
    let target = temp.0.join(world.folder_name());
    fs::create_dir(&target).unwrap();
    fs::write(target.join("player.data"), b"new player save").unwrap();
    let error = writer.add(&chunks[3]).unwrap_err();
    assert!(error.to_string().contains("refusing to replace it"));
    assert_eq!(fs::read(target.join("player.data")).unwrap(), b"new player save");
    assert!(!temp.0.join("Join.sims3.incoming").exists());
}
