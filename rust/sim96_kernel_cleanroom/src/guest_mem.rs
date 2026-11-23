#![forbid(unsafe_code)]
//! 32-bit guest-memory seam (spec card 01 §E). Every guest pointer is a `u32`;
//! reads return `Option` and never panic on unmapped / partial / wrapping
//! addresses. All address arithmetic is `wrapping`-safe (checked_sub + checked_add).

/// Read-only 32-bit guest memory. `read_u32` returns `None` for any address that
/// is not fully mapped (underflow below `base`, or a partial 4-byte read past the
/// end). The Kani base witness `0xFFFF_FFEC` reads `Some` at the edge and `None`
/// one word past it.
pub trait GuestMem32 {
    fn read_u32(&self, addr: u32) -> Option<u32>;

    /// Signed view of the same little-endian word. Provided default reinterprets
    /// the bits; implementations may override but must stay bit-identical.
    fn read_i32(&self, addr: u32) -> Option<i32> {
        self.read_u32(addr)
            .map(|bits| i32::from_le_bytes(bits.to_le_bytes()))
    }
}

/// Writable 32-bit guest memory (spec card 04: the SACS seed/draw ABI stores the
/// new LCG state through this seam). `write_u32` returns `None` on any address
/// that is not fully mapped, mirroring `read_u32`.
pub trait GuestMem32Mut: GuestMem32 {
    fn write_u32(&mut self, addr: u32, value: u32) -> Option<()>;
}

pub const fn addr32_offset(base: u32, stride: u32, idx: u32, field_off: u32) -> u32 {
    base.wrapping_add(stride.wrapping_mul(idx)).wrapping_add(field_off)
}

pub type FlatGuestMem32 = GuestBytes;

/// Reference in-memory guest: a `Vec<u8>` window at a 32-bit `base`. This is the
/// only concrete `GuestMem32`/`GuestMem32Mut` the kernel ships; the referee and
/// the tests drive the ABI functions through it.
pub struct GuestBytes {
    base: u32,
    bytes: Vec<u8>,
}

impl GuestBytes {
    pub fn new(base: u32, bytes: Vec<u8>) -> Self {
        Self { base, bytes }
    }

    /// Wrapped-safe offset of `addr` within the window. `None` when `addr` is
    /// below `base` (underflow) or the 4-byte read would run past the end.
    fn offset(&self, addr: u32) -> Option<usize> {
        addr.checked_add(3)?;
        let (rel, wrapped) = addr.overflowing_sub(self.base);
        if wrapped {
            return None;
        }
        let start = usize::try_from(rel).ok()?;
        let end = start.checked_add(4)?;
        if end > self.bytes.len() {
            return None;
        }
        Some(start)
    }
}

impl GuestMem32 for GuestBytes {
    fn read_u32(&self, addr: u32) -> Option<u32> {
        let start = self.offset(addr)?;
        let bytes: [u8; 4] = self.bytes[start..start + 4].try_into().ok()?;
        Some(u32::from_le_bytes(bytes))
    }
}

impl GuestMem32Mut for GuestBytes {
    fn write_u32(&mut self, addr: u32, value: u32) -> Option<()> {
        let start = self.offset(addr)?;
        self.bytes[start..start + 4].copy_from_slice(&value.to_le_bytes());
        Some(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guest_endianness_and_signed_bits() {
        let memory = GuestBytes::new(0x1000, vec![0xff, 0xff, 0xff, 0xff, 0x78, 0x56, 0x34, 0x12]);
        assert_eq!(memory.read_i32(0x1000), Some(-1));
        assert_eq!(memory.read_u32(0x1004), Some(0x1234_5678));
    }

    #[test]
    fn missing_partial_and_underflow_reads_are_absent() {
        let memory = GuestBytes::new(0x1000, vec![0; 4]);
        assert_eq!(memory.read_u32(0xfff), None);
        assert_eq!(memory.read_u32(0x1001), None);
        assert_eq!(memory.read_u32(u32::MAX), None);
    }

    #[test]
    fn kani_base_witness_reads_some_at_edge_none_past() {
        let memory = GuestBytes::new(0xFFFF_FFEC, vec![0, 0, 0, 0]);
        assert_eq!(memory.read_u32(0xFFFF_FFEC), Some(0));
        assert_eq!(memory.read_u32(0xFFFF_FFFF), None);
        assert_eq!(memory.read_u32(0x0000_0000), None);
    }

    #[test]
    fn write_round_trips_and_rejects_unmapped() {
        let mut memory = GuestBytes::new(0x1000, vec![0; 8]);
        assert_eq!(memory.write_u32(0x1000, 0xDEAD_BEEF), Some(()));
        assert_eq!(memory.read_u32(0x1000), Some(0xDEAD_BEEF));
        assert_eq!(memory.write_u32(0x0FFF, 1), None);
        assert_eq!(memory.write_u32(0x1004, 1), Some(()));
        assert_eq!(memory.read_u32(0x1004), Some(1));
        assert_eq!(memory.write_u32(0x1005, 2), None);
        let mut high = GuestBytes::new(u32::MAX - 1, vec![0; 8]);
        assert_eq!(high.write_u32(u32::MAX - 1, 1), None);
        assert_eq!(high.read_u32(u32::MAX - 1), None);
        assert_eq!(addr32_offset(0x1000, 0x18, 0x0AAA_AAAB, 4), 0x100c);
    }
}
