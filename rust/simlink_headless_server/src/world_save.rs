#![forbid(unsafe_code)]

use std::cmp::Ordering;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::path::{Path, PathBuf};

pub const WORLD_CHUNK_RAW_BYTES: usize = 262_144;
pub const WORLD_CHUNK_B64_BYTES: usize = 349_528;
pub const MARKER_FILE_NAME: &str = "simsonline-world.txt";
const _: () = assert!(WORLD_CHUNK_RAW_BYTES == 262_144);
const _: () = assert!(WORLD_CHUNK_B64_BYTES == 349_528);

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WorldSaveFile {
    pub name: String,
    pub data: Vec<u8>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WorldSave {
    pub save_name: String,
    pub files: Vec<WorldSaveFile>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WorldSaveChunk {
    pub folder_name: String,
    pub file_index: usize,
    pub file_count: usize,
    pub file_name: String,
    pub offset: u64,
    pub file_length: u64,
    pub data: Vec<u8>,
}

/// Ordinal casing uses one code point, not expanding or locale-sensitive casing.
pub fn ordinal_ignore_case_cmp(a: &str, b: &str) -> Ordering {
    fn upper(text: &str) -> Vec<u16> {
        text.chars()
            .map(|c| {
                let mut mapped = c.to_uppercase();
                let first = mapped.next().expect("uppercase is nonempty");
                // OrdinalIgnoreCase does not fold non-ASCII characters into ASCII.
                if mapped.next().is_some() || (!c.is_ascii() && first.is_ascii()) {
                    c
                } else {
                    first
                }
            })
            .collect::<String>()
            .encode_utf16()
            .collect()
    }
    upper(a).cmp(&upper(b))
}

impl WorldSave {
    pub fn folder_name(&self) -> String {
        if self.save_name.to_ascii_lowercase().ends_with(".sims3") {
            self.save_name.clone()
        } else {
            format!("{}.sims3", self.save_name)
        }
    }

    pub fn total_bytes(&self) -> u64 {
        self.files.iter().map(|file| file.data.len() as u64).sum()
    }

    pub fn read_folder(folder: &Path, save_name: &str) -> io::Result<Self> {
        let mut paths = Vec::new();
        for entry in fs::read_dir(folder)? {
            let entry = entry?;
            if entry.metadata()?.is_file() {
                paths.push(entry.path());
            }
        }
        paths.sort_by(|a, b| ordinal_ignore_case_cmp(&a.to_string_lossy(), &b.to_string_lossy()));
        let files = paths
            .into_iter()
            .map(|path| {
                let name = path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Bad save file name"))?
                    .to_owned();
                Ok(WorldSaveFile {
                    name,
                    data: fs::read(path)?,
                })
            })
            .collect::<io::Result<Vec<_>>>()?;
        Ok(Self {
            save_name: save_name.to_owned(),
            files,
        })
    }

    pub fn to_chunks(&self, chunk_bytes: usize) -> Vec<WorldSaveChunk> {
        assert!(chunk_bytes > 0, "chunk_bytes must be positive");
        let mut chunks = Vec::new();
        for (file_index, file) in self.files.iter().enumerate() {
            let count = file.data.len().div_ceil(chunk_bytes).max(1);
            for index in 0..count {
                let offset = index * chunk_bytes;
                let end = offset + chunk_bytes.min(file.data.len() - offset);
                chunks.push(WorldSaveChunk {
                    folder_name: self.folder_name(),
                    file_index,
                    file_count: self.files.len(),
                    file_name: file.name.clone(),
                    offset: offset as u64,
                    file_length: file.data.len() as u64,
                    data: file.data[offset..end].to_vec(),
                });
            }
        }
        chunks
    }

    /// Each file owns one EV sequence number and one bulk, including empty files.
    pub fn encode_world_file_lines(
        &self,
        seq_start: u64,
        tick: u64,
        corr: u64,
        bulk_id_start: u64,
    ) -> Vec<String> {
        let mut lines = Vec::new();
        let folder = crate::messages::hex_encode(&self.folder_name());
        for (file_index, file) in self.files.iter().enumerate() {
            let seq = seq_start + file_index as u64;
            let bulk_id = bulk_id_start + file_index as u64;
            lines.push(format!(
                "EV|{seq}|{tick}|{corr}|WorldFile|{folder}|{file_index}|{}|{}|{}|b{bulk_id}",
                self.files.len(),
                crate::messages::hex_encode(&file.name),
                file.data.len()
            ));
            let count = file.data.len().div_ceil(WORLD_CHUNK_RAW_BYTES).max(1);
            for index in 0..count {
                let offset = index * WORLD_CHUNK_RAW_BYTES;
                let end = offset + WORLD_CHUNK_RAW_BYTES.min(file.data.len() - offset);
                lines.push(format!(
                    "BULK|{bulk_id}|{index}|{count}|{}",
                    crate::wire_v1::b64_encode(&file.data[offset..end])
                ));
            }
        }
        lines
    }
}

/// Client-side staging writer. The server only reads TS3-authored save content.
pub struct WorldSaveWriter {
    saves_directory: PathBuf,
    folder: Option<String>,
    staging: Option<PathBuf>,
    current: Option<File>,
    current_index: Option<usize>,
    current_name: String,
    current_length: u64,
    current_offset: u64,
    file_count: usize,
    complete: bool,
    written: u64,
}

impl WorldSaveWriter {
    pub fn new(saves_directory: PathBuf) -> Self {
        Self {
            saves_directory,
            folder: None,
            staging: None,
            current: None,
            current_index: None,
            current_name: String::new(),
            current_length: 0,
            current_offset: 0,
            file_count: 0,
            complete: false,
            written: 0,
        }
    }

    pub fn is_complete(&self) -> bool {
        self.complete
    }

    pub fn bytes_written(&self) -> u64 {
        self.written
    }

    pub fn folder_name(&self) -> Option<&str> {
        self.folder.as_deref().filter(|_| self.complete)
    }

    pub fn abort(&mut self) {
        self.current = None;
        if let Some(staging) = self.staging.take() {
            let _ = fs::remove_dir_all(staging);
        }
    }

    pub fn add(&mut self, chunk: &WorldSaveChunk) -> io::Result<()> {
        let result = self.add_core(chunk);
        if result.is_err() {
            self.abort();
        }
        result
    }

    fn add_core(&mut self, chunk: &WorldSaveChunk) -> io::Result<()> {
        fn invalid(message: impl Into<String>) -> io::Error {
            io::Error::new(io::ErrorKind::InvalidData, message.into())
        }
        fn valid_name(name: &str) -> bool {
            !name.is_empty()
                && name != "."
                && name != ".."
                && !name.chars().any(|c| c < ' ' || "<>:\"/\\|?*\0".contains(c))
        }
        if self.complete {
            return Err(invalid("The world save is already complete."));
        }
        if self.folder.is_none() {
            if !valid_name(&chunk.folder_name)
                || !chunk.folder_name.to_ascii_lowercase().ends_with(".sims3")
            {
                return Err(invalid(format!("Bad save folder name: {}", chunk.folder_name)));
            }
            let target = self.saves_directory.join(&chunk.folder_name);
            if target.is_dir() && !target.join(MARKER_FILE_NAME).is_file() {
                return Err(io::Error::other(format!(
                    "{} exists and wasn't written by SimsOnline; refusing to replace it.",
                    target.display()
                )));
            }
            let staging = self
                .saves_directory
                .join(format!("{}.incoming", chunk.folder_name));
            if staging.is_dir() {
                fs::remove_dir_all(&staging)?;
            }
            self.staging = Some(staging.clone());
            fs::create_dir_all(staging)?;
            self.folder = Some(chunk.folder_name.clone());
            self.file_count = chunk.file_count;
        }
        if self.folder.as_deref() != Some(&chunk.folder_name)
            || chunk.file_count == 0
            || chunk.file_count != self.file_count
            || chunk.file_index >= chunk.file_count
        {
            return Err(invalid("Chunk folder or file count changed."));
        }
        if self.current_index != Some(chunk.file_index) {
            let next_index = self.current_index.map_or(0, |index| index + 1);
            if chunk.file_index != next_index
                || chunk.offset != 0
                || self.current_offset != self.current_length
            {
                return Err(invalid("Out-of-order chunk."));
            }
            if !valid_name(&chunk.file_name)
                || chunk.file_name.eq_ignore_ascii_case(MARKER_FILE_NAME)
            {
                return Err(invalid(format!("Bad save file name: {}", chunk.file_name)));
            }
            self.current = None;
            self.current = Some(
                OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(self.staging.as_ref().expect("staging exists").join(&chunk.file_name))?,
            );
            self.current_index = Some(chunk.file_index);
            self.current_name.clone_from(&chunk.file_name);
            self.current_length = chunk.file_length;
            self.current_offset = 0;
        }
        if chunk.offset != self.current_offset
            || chunk.file_name != self.current_name
            || chunk.file_length != self.current_length
            || chunk.data.len() as u64 > self.current_length.saturating_sub(self.current_offset)
        {
            return Err(invalid("Chunk offset or length doesn't follow the current file."));
        }
        self.current
            .as_mut()
            .expect("current file exists")
            .write_all(&chunk.data)?;
        self.current_offset += chunk.data.len() as u64;
        self.written += chunk.data.len() as u64;
        if chunk.file_index == self.file_count - 1 && self.current_offset == self.current_length {
            self.current = None;
            let staging = self.staging.as_ref().expect("staging exists");
            fs::write(
                staging.join(MARKER_FILE_NAME),
                "Written by SimsOnline from the server's world.\n",
            )?;
            let target = self.saves_directory.join(self.folder.as_ref().expect("folder exists"));
            // Recheck at the destructive boundary, not only when staging began.
            if target.is_dir() {
                if !target.join(MARKER_FILE_NAME).is_file() {
                    return Err(io::Error::other(format!(
                        "{} exists and wasn't written by SimsOnline; refusing to replace it.",
                        target.display()
                    )));
                }
                fs::remove_dir_all(&target)?;
            }
            fs::rename(staging, target)?;
            self.staging = None;
            self.complete = true;
        }
        Ok(())
    }
}

impl Drop for WorldSaveWriter {
    fn drop(&mut self) {
        self.abort();
    }
}
