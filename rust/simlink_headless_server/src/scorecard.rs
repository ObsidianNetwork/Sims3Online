#![forbid(unsafe_code)]
//! §K source counts and observed offline transcript scorecard.

use std::{fs, io, path::Path};
use crate::bridge_driver::PUBLIC_VERBS;

pub fn source_counts(root: &Path) -> io::Result<(usize, usize, usize, usize)> {
    // The protocol was moved into its own assembly. Read the old path if it
    // exists on an earlier base, otherwise the current canonical definition.
    let old = root.join("DedicatedServer/Protocol/ClientCommand.cs");
    let public = fs::read_to_string(if old.exists() { old } else { root.join("SimsOnline.Protocol/ClientCommand.cs") })?;
    let names: Vec<_> = public.lines().filter_map(|line| {
        line.trim().strip_prefix("[JsonDerivedType(typeof(")?.split_once(')').map(|(name, _)| name)
    }).collect();
    let classified = names.iter().filter(|name| PUBLIC_VERBS.iter().filter(|(verb, _)| verb == *name).count() == 1).count();
    let kernel = fs::read_to_string(root.join("rust/sim96_kernel_cleanroom/src/runtime_bridge.rs"))?;
    let routes = kernel.split("pub const AUTHORITY_ROUTES").nth(1)
        .and_then(|tail| tail.split_once("];")).map(|(table, _)| table)
        .ok_or_else(|| io::Error::other("AUTHORITY_ROUTES not found"))?;
    Ok((names.len(), classified, routes.matches("AuthorityOwner::").count(), routes.matches("AuthorityOwner::NativeTs3W").count()))
}

/// Tab-separated case/request/reply rows. A missing or uncorrelated named reply
/// is a drop, rather than a hardcoded scorecard zero.
pub fn unanswered_pairs(transcript: &str) -> usize {
    transcript.lines().filter(|line| {
        let parts: Vec<_> = line.split('\t').collect();
        if parts.len() < 2 || !parts[1].starts_with("CMD|") { return false; }
        let corr = parts[1].split('|').nth(1).unwrap_or("");
        !parts.get(2).is_some_and(|reply| {
            (reply.starts_with("ACK|") || reply.starts_with("ERR|")) && reply.split('|').nth(1) == Some(corr)
        })
    }).count()
}

pub fn emit(root: &Path, dir: &Path) -> io::Result<()> {
    let (public, classified, total, native) = source_counts(root)?;
    if public != classified || total != 30 || native != 3 {
        return Err(io::Error::other("Card 57 source count invariant failed"));
    }
    let mut drops = 0;
    for name in ["reply_contracts.txt", "admin_and_supersede.txt"] {
        let text = fs::read_to_string(dir.join(name))?;
        drops += unanswered_pairs(&text);
    }
    let posture = fs::read_to_string(dir.join("bind_posture.txt"))?;
    let non_loopback = posture.lines().find_map(|line| line.strip_prefix("non_loopback_default="))
        .and_then(|n| n.parse::<usize>().ok()).ok_or_else(|| io::Error::other("missing bind observation"))?;
    fs::write(dir.join("scorecard.txt"), format!(
        "verbs_public={public}|verbs_classified={classified}|rust_plus_native={total}|native={native}|silent_drops={drops}|non_loopback_default={non_loopback}\n"
    ))
}
