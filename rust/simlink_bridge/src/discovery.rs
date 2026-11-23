//! Region discovery, copied from the oracle (card 56 §B.3): `ProcessMemoryBridge.cs` `FindBridgeBuffer`,
//! `SearchRegion`, `SearchBulkMarkerInRegion`, `IsCandidateBuffer`, `ValidateV11Candidate`,
//! `ValidateBulkSubRegion`. The order of acceptance is the oracle's; gate 3 checks agreement on a live
//! process. Failure is the named refusal `region_not_found` (or `process_exited`), never a silent retry.
#![forbid(unsafe_code)]

use std::io::BufRead;
use std::time::{Duration, Instant};

use crate::error::BridgeError;
use crate::layout::{
    bulk_marker, bulk_region_base_offset, control_marker, le_u32, BulkRegionHeader, BULK_HEADER_SIZE,
    CONTROL_SLOT_COUNT, CTL_SLOT_BASE, CTL_SLOT_COUNT, HOST_SLOT_BASE, HOST_SLOT_COUNT, LAYOUT_VERSION,
    LAYOUT_VERSION_OFFSET, MARKER_LEN, SLOT_HEADER_SIZE, SLOT_SIZE,
};
use crate::memory::GuestMemory;
pub use crate::memory::SCAN_CEILING;

/// `SearchRegion` reads in 1 MiB chunks with a 31-byte carry.
pub const SCAN_CHUNK: usize = 1024 * 1024;
/// `ConnectAsync` waits 250 ms between scans.
pub const RESCAN_DELAY: Duration = Duration::from_millis(250);
const PROBE_LEN: usize = 256;

/// Scan until a region is found or `deadline` passes (the deadline is the caller's, §F).
pub fn find_region<M: GuestMemory>(mem: &M, deadline: Instant) -> Result<u32, BridgeError> {
    loop {
        if mem.has_exited() {
            return Err(BridgeError::ProcessExited);
        }
        if let Some(base) = scan(mem, Some(deadline))? {
            return Ok(base);
        }
        let now = Instant::now();
        if now >= deadline {
            return Err(BridgeError::RegionNotFound);
        }
        std::thread::sleep(RESCAN_DELAY.min(deadline - now));
    }
}

/// One full walk of the address space; `Ok(None)` when nothing was accepted.
pub fn find_region_once<M: GuestMemory>(mem: &M) -> Result<Option<u32>, BridgeError> {
    scan(mem, None)
}

fn exited(e: &BridgeError) -> bool {
    matches!(e, BridgeError::ProcessExited)
}

fn scan<M: GuestMemory>(mem: &M, deadline: Option<Instant>) -> Result<Option<u32>, BridgeError> {
    for region in mem.regions()? {
        if deadline.is_some_and(|d| Instant::now() >= d) {
            return Ok(None);
        }
        if region.size >= SLOT_SIZE as u64 && region.readable_writable {
            if let Some(found) = search_region(mem, region.base, region.size)? {
                return Ok(Some(found));
            }
        }
    }
    Ok(None)
}

/// Reads one chunk; `None` on any failure (the oracle skips the chunk and resets its carry).
fn read_chunk<M: GuestMemory>(mem: &M, addr: u64, len: usize) -> Result<Option<Vec<u8>>, BridgeError> {
    let Ok(a) = u32::try_from(addr) else { return Ok(None) };
    let mut buf = vec![0u8; len];
    match mem.read(a, &mut buf) {
        Ok(()) => Ok(Some(buf)),
        Err(e) if exited(&e) => Err(BridgeError::ProcessExited),
        Err(_) => Ok(None),
    }
}

/// Every index of `needle` in `hay`, ascending, from `start`.
fn index_of(hay: &[u8], needle: &[u8], start: usize) -> Option<usize> {
    let last = hay.len().checked_sub(needle.len())?;
    if start > last {
        return None;
    }
    let Some(&first) = needle.first() else { return Some(start) };
    let mut candidates = &hay[start..=last];
    while !candidates.is_empty() {
        // The standard library's byte search stays fast even in an unoptimized bridge build.
        candidates.skip_until(first).expect("reading a byte slice cannot fail");
        let i = last - candidates.len();
        if hay[i] == first && &hay[i..i + needle.len()] == needle {
            return Some(i);
        }
    }
    None
}

/// `SearchRegion`/`SearchBulkMarkerInRegion` share the chunk-and-carry loop.
fn search_chunks<M: GuestMemory>(
    mem: &M,
    base: u64,
    size: u64,
    marker: &[u8; MARKER_LEN],
    mut accept: impl FnMut(u64) -> Result<Option<u32>, BridgeError>,
) -> Result<Option<u32>, BridgeError> {
    let mut carry: Vec<u8> = Vec::with_capacity(MARKER_LEN - 1);
    let mut offset: u64 = 0;
    while offset < size {
        let to_read = (SCAN_CHUNK as u64).min(size - offset) as usize;
        let Some(chunk) = read_chunk(mem, base + offset, to_read)? else {
            offset += to_read as u64;
            carry.clear();
            continue;
        };
        let mut hay = Vec::with_capacity(carry.len() + chunk.len());
        hay.extend_from_slice(&carry);
        hay.extend_from_slice(&chunk);
        let hay_base = base + offset - carry.len() as u64;
        let mut start = 0;
        while let Some(i) = index_of(&hay, marker, start) {
            if let Some(found) = accept(hay_base + i as u64)? {
                return Ok(Some(found));
            }
            start = i + 1;
        }
        let keep = (MARKER_LEN - 1).min(hay.len());
        carry = hay[hay.len() - keep..].to_vec();
        offset += chunk.len() as u64;
    }
    Ok(None)
}

fn search_region<M: GuestMemory>(mem: &M, base: u64, size: u64) -> Result<Option<u32>, BridgeError> {
    let marker = control_marker();
    if let Some(found) = search_chunks(mem, base, size, &marker, |candidate| {
        Ok(if is_candidate_buffer(mem, candidate)? { u32::try_from(candidate).ok() } else { None })
    })? {
        return Ok(Some(found));
    }
    search_bulk_marker_in_region(mem, base, size)
}

fn search_bulk_marker_in_region<M: GuestMemory>(mem: &M, base: u64, size: u64) -> Result<Option<u32>, BridgeError> {
    if size < MARKER_LEN as u64 {
        return Ok(None);
    }
    let bulk_base = bulk_region_base_offset() as u64;
    if size <= (BULK_HEADER_SIZE + MARKER_LEN) as u64 && base > bulk_base {
        let anchored = base - bulk_base;
        if validate_bulk_sub_region(mem, anchored)? {
            return Ok(u32::try_from(anchored).ok());
        }
    }
    let marker = bulk_marker();
    search_chunks(mem, base, size, &marker, |hit| {
        if hit > bulk_base {
            let candidate = hit - bulk_base;
            if validate_bulk_sub_region(mem, candidate)? {
                return Ok(u32::try_from(candidate).ok());
            }
        }
        Ok(None)
    })
}

/// `IsCandidateBuffer`: v11 header first, then a valid bulk sub-region, then the bare marker plus zeros.
fn is_candidate_buffer<M: GuestMemory>(mem: &M, candidate: u64) -> Result<bool, BridgeError> {
    let Some(probe) = read_chunk(mem, candidate, PROBE_LEN)? else { return Ok(false) };
    if le_u32(&probe, LAYOUT_VERSION_OFFSET) == LAYOUT_VERSION && validate_v11_candidate(mem, candidate, &probe)? {
        return Ok(true);
    }
    if validate_bulk_sub_region(mem, candidate)? {
        return Ok(true);
    }
    Ok(probe[..MARKER_LEN] == control_marker() && probe[MARKER_LEN..].iter().all(|b| *b == 0))
}

fn validate_v11_candidate<M: GuestMemory>(mem: &M, candidate: u64, probe: &[u8]) -> Result<bool, BridgeError> {
    let expect = [
        (36, CONTROL_SLOT_COUNT),
        (40, SLOT_SIZE),
        (44, SLOT_HEADER_SIZE),
        (48, HOST_SLOT_BASE),
        (52, HOST_SLOT_COUNT),
        (56, CTL_SLOT_BASE),
        (60, CTL_SLOT_COUNT),
    ];
    if expect.iter().any(|(at, v)| le_u32(probe, *at) != *v as u32) {
        return Ok(false);
    }
    validate_bulk_sub_region(mem, candidate)
}

/// `ValidateBulkSubRegion`: the 64-byte bulk header at `candidate + bulk_region_base_offset()`.
pub fn validate_bulk_sub_region<M: GuestMemory>(mem: &M, candidate: u64) -> Result<bool, BridgeError> {
    let Some(header) = read_chunk(mem, candidate + bulk_region_base_offset() as u64, BULK_HEADER_SIZE)? else {
        return Ok(false);
    };
    Ok(BulkRegionHeader::is_valid_v11(&header))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::{ControlRegionHeader, TOTAL_REGION_SIZE};
    use crate::memory::SimMemory;

    fn plant_v11(m: &SimMemory, base: u32) {
        m.poke(base, &ControlRegionHeader::v11().to_bytes());
        m.poke(base + bulk_region_base_offset() as u32, &BulkRegionHeader::v11().to_bytes());
    }

    #[test]
    fn index_of_handles_empty_inputs_and_out_of_range_starts() {
        assert_eq!(index_of(b"", b"", 0), Some(0));
        assert_eq!(index_of(b"", b"", 1), None);
        assert_eq!(index_of(b"", b"S", 0), None);
        assert_eq!(index_of(b"S", b"SS", 0), None);
        assert_eq!(index_of(b"SIM", b"", 0), Some(0));
        assert_eq!(index_of(b"SIM", b"", 3), Some(3));
        assert_eq!(index_of(b"SIM", b"", 4), None);
        assert_eq!(index_of(b"SIM", b"S", usize::MAX), None);
    }

    #[test]
    fn index_of_finds_both_boundaries_and_respects_start() {
        let needle = control_marker();
        let mut hay = needle.to_vec();
        hay.extend_from_slice(&[0; 64]);
        hay.extend_from_slice(&needle);
        let last = hay.len() - needle.len();
        assert_eq!(index_of(&hay, &needle, 0), Some(0));
        assert_eq!(index_of(&hay, &needle, 1), Some(last));
        assert_eq!(index_of(&hay, &needle, last), Some(last));
        assert_eq!(index_of(&hay, &needle, last + 1), None);
        assert_eq!(index_of(&needle, &needle, 0), Some(0));
        assert_eq!(index_of(b"aaaa", b"aa", 1), Some(1));
        assert_eq!(index_of(b"aaaa", b"a", 3), Some(3));
        assert_eq!(index_of(b"abcdef", b"xy", 0), None);
        assert_eq!(index_of(b"abcx", b"xy", 0), None);
        assert_eq!(index_of(&[0xff, 0x80, 0, 0xff], &[0xff], 1), Some(3));
    }

    #[test]
    fn index_of_skips_repeated_first_byte_false_positives() {
        let needle = control_marker();
        let mut hay = vec![needle[0]; 64];
        hay.extend_from_slice(&needle);
        assert_eq!(index_of(&hay, &needle, 0), Some(64));
        assert_eq!(index_of(&hay, &needle, 65), None);
    }

    #[test]
    fn index_of_scans_large_buffers_quickly_even_in_debug() {
        let needle = control_marker();
        for (len, budget) in [
            (16 * 1024 * 1024, Duration::from_millis(100)),
            (1024 * 1024 * 1024 + 64, Duration::from_secs(1)),
        ] {
            let mut hay = vec![0; len];
            // Sparse false positives exercise candidate rejection as well as the fast skip.
            for i in (0..len).step_by(4096) {
                hay[i] = needle[0];
            }
            let last = len - needle.len();
            hay[last..].copy_from_slice(&needle);
            let started = Instant::now();
            assert_eq!(index_of(std::hint::black_box(&hay), &needle, 0), Some(last));
            let elapsed = started.elapsed();
            eprintln!("index_of: {len} bytes in {elapsed:?}");
            assert!(elapsed < budget, "{len}-byte scan took {elapsed:?}, budget {budget:?}");
        }
    }

    #[test]
    fn finds_a_v11_region_at_an_unaligned_offset_inside_a_mapping() {
        let m = SimMemory::new();
        m.map(0x0100_0000, TOTAL_REGION_SIZE + 0x3000, true);
        let base = 0x0100_0000 + 0x1234;
        plant_v11(&m, base);
        assert_eq!(find_region_once(&m), Ok(Some(base)));
    }

    #[test]
    fn finds_a_marker_split_across_a_chunk_boundary() {
        let m = SimMemory::new();
        m.map(0x0200_0000, 3 * SCAN_CHUNK, true);
        // The marker starts 10 bytes before the first 1 MiB boundary; a bare marker plus zeros is accepted.
        let base = 0x0200_0000 + SCAN_CHUNK as u32 - 10;
        m.poke(base, &control_marker());
        assert_eq!(find_region_once(&m), Ok(Some(base)));
    }

    #[test]
    fn a_bare_marker_with_noise_is_rejected() {
        let m = SimMemory::new();
        m.map(0x0300_0000, 0x10000, true);
        m.poke(0x0300_0000, &control_marker());
        m.poke(0x0300_0000 + 100, &[1]);
        assert_eq!(find_region_once(&m), Ok(None));
    }

    #[test]
    fn a_warm_region_with_a_stomped_control_header_is_found_by_its_bulk_header() {
        let m = SimMemory::new();
        m.map(0x0400_0000, TOTAL_REGION_SIZE, true);
        plant_v11(&m, 0x0400_0000);
        // A compat frame stomped bytes 0..48: no marker left; the bulk-marker pass recovers the base.
        m.poke(0x0400_0000, &[0x55u8; 48]);
        assert_eq!(find_region_once(&m), Ok(Some(0x0400_0000)));
    }

    #[test]
    fn regions_not_readable_writable_are_skipped() {
        let m = SimMemory::new();
        m.map(0x0500_0000, TOTAL_REGION_SIZE, false);
        plant_v11(&m, 0x0500_0000);
        assert_eq!(find_region_once(&m), Ok(None));
    }

    #[test]
    fn regions_above_the_32_bit_ceiling_are_not_walked() {
        let m = SimMemory::new();
        m.map(0x8000_0000, TOTAL_REGION_SIZE, true);
        plant_v11(&m, 0x8000_0000);
        assert_eq!(find_region_once(&m), Ok(None));
    }

    #[test]
    fn deadline_expiry_is_region_not_found_and_exit_is_process_exited() {
        let m = SimMemory::new();
        m.map(0x0600_0000, 0x2000, true);
        assert_eq!(find_region(&m, Instant::now()), Err(BridgeError::RegionNotFound));
        m.set_exited(true);
        assert_eq!(find_region(&m, Instant::now() + Duration::from_secs(5)), Err(BridgeError::ProcessExited));
    }
}
