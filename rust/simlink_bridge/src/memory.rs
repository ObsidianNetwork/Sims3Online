//! The guest-memory seam (card 56 §D "The memory seam"): u32 guest addresses, checked arithmetic, and a
//! typed error on a short or failed access, as card 01 §E's `GuestMem32` reads (`None` on an unmapped,
//! partial or wrapping address). This crate does not depend on the kernel; card 57's daemon holds the one
//! adapter that implements `GuestMem32`/`GuestMem32Mut` over [`GuestMemory`].
//!
//! Two implementations: [`crate::ffi::ProcessAttach`] (a live target) and [`SimMemory`] (an in-process
//! address space for tests and for replaying recorded regions).
#![forbid(unsafe_code)]

use std::cell::{Cell, RefCell};

use crate::error::BridgeError;

/// One mapping of the target's address space, as `VirtualQueryEx` describes it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RegionInfo {
    pub base: u64,
    pub size: u64,
    /// Committed, private, read/write and not a guard page (`NativeProcessMemory.cs` `IsReadableWritable`).
    pub readable_writable: bool,
}

/// Safe access to a 32-bit guest address space.
pub trait GuestMemory {
    /// Fill `buf` from `addr`, or fail; a short copy is `BridgeError::ShortRead`, never a zero fill.
    fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError>;
    /// Write all of `data` at `addr`, or fail; never retried.
    ///
    /// Not a way to publish a bridge command. A command line goes through the driver
    /// (`crate::driver::BridgeDriver::send_line`, `send_compat_line` or `send_command`), where card 56 §E.3
    /// refuses the `APPLY_*` verbs before any write; that is why `BridgeDriver::memory` hands out
    /// `GuestMemoryView` (reads only) instead of `&M`. This method is the implementation seam a target sits
    /// behind (a live process, a replayed region), used by the driver itself and by card 57's adapter.
    fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError>;
    /// The target's mappings from address 0 up to [`SCAN_CEILING`], as the oracle's `VirtualQueryEx` walk
    /// visits them (`ProcessMemoryBridge.cs` `FindBridgeBuffer`).
    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError>;
    /// True once the target has exited.
    fn has_exited(&self) -> bool;

    fn read_u32(&self, addr: u32) -> Result<u32, BridgeError> {
        let mut b = [0u8; 4];
        self.read(addr, &mut b)?;
        Ok(u32::from_le_bytes(b))
    }

    fn read_u64(&self, addr: u32) -> Result<u64, BridgeError> {
        let mut b = [0u8; 8];
        self.read(addr, &mut b)?;
        Ok(u64::from_le_bytes(b))
    }
}

impl<T: GuestMemory + ?Sized> GuestMemory for &T {
    fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
        (**self).read(addr, buf)
    }
    fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError> {
        (**self).write(addr, data)
    }
    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> {
        (**self).regions()
    }
    fn has_exited(&self) -> bool {
        (**self).has_exited()
    }
}

/// TS3W is 32-bit: the walk stops past this address (`FindBridgeBuffer`).
pub const SCAN_CEILING: u64 = 0x7FFF_FFFF;

/// The oracle's walk over a per-address query: from 0, the mapping at the address, then the next address
/// `base + max(size, 0x1000)`, until the query reports the end, fails, or the next address passes the
/// ceiling or does not advance. A process exit is an error; any other failure ends the walk (the oracle
/// stops scanning).
pub fn walk_regions(query: impl Fn(u32) -> Result<Option<RegionInfo>, BridgeError>) -> Result<Vec<RegionInfo>, BridgeError> {
    let mut out = Vec::new();
    let mut address: u64 = 0;
    while let Ok(a) = u32::try_from(address) {
        let region = match query(a) {
            Ok(Some(r)) => r,
            Ok(None) => break,
            Err(BridgeError::ProcessExited) => return Err(BridgeError::ProcessExited),
            Err(_) => break,
        };
        out.push(region);
        let next = region.base.saturating_add(region.size.max(0x1000));
        if next <= address || next > SCAN_CEILING {
            break;
        }
        address = next;
    }
    Ok(out)
}

/// `addr + len` as a guest address range end, checked.
pub fn checked_end(addr: u32, len: usize) -> Result<u64, BridgeError> {
    let end = u64::from(addr).checked_add(len as u64).ok_or(BridgeError::AddressOverflow { addr, len })?;
    if end > u64::from(u32::MAX) + 1 {
        return Err(BridgeError::AddressOverflow { addr, len });
    }
    Ok(end)
}

/// `base + offset` as a guest address, checked.
pub fn guest_addr(base: u32, offset: usize) -> Result<u32, BridgeError> {
    u32::try_from(offset)
        .ok()
        .and_then(|o| base.checked_add(o))
        .ok_or(BridgeError::AddressOverflow { addr: base, len: offset })
}

struct SimRegion {
    base: u32,
    bytes: Vec<u8>,
    readable_writable: bool,
}

/// An in-process guest address space: a sorted set of mappings, nothing else.
pub struct SimMemory {
    regions: RefCell<Vec<SimRegion>>,
    exited: Cell<bool>,
    writes: Cell<usize>,
}

impl Default for SimMemory {
    fn default() -> Self {
        Self::new()
    }
}

impl SimMemory {
    pub fn new() -> Self {
        Self { regions: RefCell::new(Vec::new()), exited: Cell::new(false), writes: Cell::new(0) }
    }

    /// Map `len` zero bytes at `base`. Overlapping mappings are a caller bug and panic.
    pub fn map(&self, base: u32, len: usize, readable_writable: bool) {
        let end = checked_end(base, len).expect("mapping fits the guest space");
        let mut regions = self.regions.borrow_mut();
        for r in regions.iter() {
            let r_end = u64::from(r.base) + r.bytes.len() as u64;
            assert!(end <= u64::from(r.base) || u64::from(base) >= r_end, "overlapping mapping");
        }
        regions.push(SimRegion { base, bytes: vec![0u8; len], readable_writable });
        regions.sort_by_key(|r| r.base);
    }

    pub fn set_exited(&self, exited: bool) {
        self.exited.set(exited);
    }

    /// How many successful writes this space has taken (a test's witness that nothing was retried).
    pub fn write_count(&self) -> usize {
        self.writes.get()
    }

    /// Direct poke, bypassing the exit flag (the "mod" side of a test).
    pub fn poke(&self, addr: u32, data: &[u8]) {
        self.copy(addr, data.len(), |slice| slice.copy_from_slice(data)).expect("poke inside a mapping");
    }

    /// Direct peek, bypassing the exit flag.
    pub fn peek(&self, addr: u32, len: usize) -> Vec<u8> {
        let mut out = vec![0u8; len];
        self.copy(addr, len, |slice| out.copy_from_slice(slice)).expect("peek inside a mapping");
        out
    }

    fn copy(&self, addr: u32, len: usize, f: impl FnOnce(&mut [u8])) -> Result<(), BridgeError> {
        let end = checked_end(addr, len)?;
        let mut regions = self.regions.borrow_mut();
        for r in regions.iter_mut() {
            let r_start = u64::from(r.base);
            let r_end = r_start + r.bytes.len() as u64;
            if u64::from(addr) >= r_start && u64::from(addr) < r_end {
                if end > r_end {
                    return Err(BridgeError::ShortRead { addr, want: len, got: (r_end - u64::from(addr)) as usize });
                }
                let start = (u64::from(addr) - r_start) as usize;
                f(&mut r.bytes[start..start + len]);
                return Ok(());
            }
        }
        if len == 0 {
            return Ok(());
        }
        Err(BridgeError::ShortRead { addr, want: len, got: 0 })
    }
}

impl GuestMemory for SimMemory {
    fn read(&self, addr: u32, buf: &mut [u8]) -> Result<(), BridgeError> {
        if self.exited.get() {
            return Err(BridgeError::ProcessExited);
        }
        let len = buf.len();
        self.copy(addr, len, |slice| buf.copy_from_slice(slice))
    }

    fn write(&self, addr: u32, data: &[u8]) -> Result<(), BridgeError> {
        if self.exited.get() {
            return Err(BridgeError::ProcessExited);
        }
        match self.copy(addr, data.len(), |slice| slice.copy_from_slice(data)) {
            Ok(()) => {
                self.writes.set(self.writes.get() + 1);
                Ok(())
            }
            Err(BridgeError::ShortRead { addr, .. }) => Err(BridgeError::WriteFailed { addr }),
            Err(e) => Err(e),
        }
    }

    fn regions(&self) -> Result<Vec<RegionInfo>, BridgeError> {
        walk_regions(|a| self.query(a))
    }

    fn has_exited(&self) -> bool {
        self.exited.get()
    }
}

impl SimMemory {
    /// The mapping containing `addr`, the free gap before the next one, or `None` past the last.
    pub fn query(&self, addr: u32) -> Result<Option<RegionInfo>, BridgeError> {
        if self.exited.get() {
            return Err(BridgeError::ProcessExited);
        }
        let regions = self.regions.borrow();
        let a = u64::from(addr);
        for r in regions.iter() {
            let r_start = u64::from(r.base);
            let r_end = r_start + r.bytes.len() as u64;
            if a >= r_start && a < r_end {
                return Ok(Some(RegionInfo { base: r_start, size: r_end - r_start, readable_writable: r.readable_writable }));
            }
            if r_start > a {
                // The free gap before the next mapping, as VirtualQueryEx reports MEM_FREE.
                return Ok(Some(RegionInfo { base: a, size: r_start - a, readable_writable: false }));
            }
        }
        Ok(None)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn short_and_unmapped_reads_are_typed() {
        let m = SimMemory::new();
        m.map(0x1000, 16, true);
        let mut b = [0u8; 8];
        assert_eq!(m.read(0x100C, &mut b), Err(BridgeError::ShortRead { addr: 0x100C, want: 8, got: 4 }));
        assert_eq!(m.read(0x5000, &mut b), Err(BridgeError::ShortRead { addr: 0x5000, want: 8, got: 0 }));
        assert_eq!(m.read(u32::MAX - 2, &mut b), Err(BridgeError::AddressOverflow { addr: u32::MAX - 2, len: 8 }));
        m.set_exited(true);
        assert_eq!(m.read(0x1000, &mut b), Err(BridgeError::ProcessExited));
    }

    #[test]
    fn query_walks_mappings_and_gaps() {
        let m = SimMemory::new();
        m.map(0x2000, 0x1000, true);
        assert_eq!(m.query(0).unwrap(), Some(RegionInfo { base: 0, size: 0x2000, readable_writable: false }));
        assert_eq!(m.query(0x2800).unwrap(), Some(RegionInfo { base: 0x2000, size: 0x1000, readable_writable: true }));
        assert_eq!(m.query(0x3000).unwrap(), None);
        assert_eq!(
            m.regions().unwrap(),
            vec![
                RegionInfo { base: 0, size: 0x2000, readable_writable: false },
                RegionInfo { base: 0x2000, size: 0x1000, readable_writable: true },
            ]
        );
        m.map(0x8000_0000, 0x1000, true);
        assert_eq!(m.regions().unwrap().len(), 3, "the gap up to the ceiling, not the mapping past it");
    }

    #[test]
    fn guest_addr_is_checked() {
        assert_eq!(guest_addr(0x10, 0x20), Ok(0x30));
        assert!(guest_addr(u32::MAX, 1).is_err());
        assert!(checked_end(u32::MAX, 1).is_ok());
        assert!(checked_end(u32::MAX, 2).is_err());
    }
}
