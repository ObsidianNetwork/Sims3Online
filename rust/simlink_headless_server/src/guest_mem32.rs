#![forbid(unsafe_code)]
//! The kernel `GuestMem32` / `GuestMem32Mut` adapter over card 56's `ProcessAttach` (card 57 §A).
//!
//! The daemon holds the one adapter that reads the server TS3W's 32-bit guest memory through the
//! bridge crate. The `unsafe` stays inside `simlink_bridge/src/ffi.rs`; this adapter is safe and
//! forbids `unsafe_code`. It is generic over a small [`GuestProcess`] seam so the kernel contract
//! can be tested offline with an in-memory fake; the live lane drives it through a
//! [`ProcessAttach`] the caller has already attached. This crate never attaches to a process here.

use sim96_kernel_cleanroom::guest_mem::{GuestMem32, GuestMem32Mut};
use simlink_bridge::ProcessAttach;

/// The 32-bit guest read/write seam the adapter needs. [`ProcessAttach`] implements it; a test
/// supplies an in-memory fake so the adapter's kernel-contract behaviour is proven offline.
/// Each method returns `None` when the address is not fully mapped.
pub trait GuestProcess {
    /// Read `buf.len()` bytes at the absolute guest address `addr`, or `None` if unmapped.
    fn read_guest(&self, addr: u32, buf: &mut [u8]) -> Option<()>;
    /// Write `bytes` at the absolute guest address `addr`, or `None` if unmapped.
    fn write_guest(&mut self, addr: u32, bytes: &[u8]) -> Option<()>;
}

impl GuestProcess for ProcessAttach {
    fn read_guest(&self, addr: u32, buf: &mut [u8]) -> Option<()> {
        self.read(addr, buf).ok()
    }

    fn write_guest(&mut self, addr: u32, bytes: &[u8]) -> Option<()> {
        self.write(addr, bytes).ok()
    }
}

/// A kernel `GuestMem32`/`GuestMem32Mut` over a [`GuestProcess`]. Reads return `None` for any
/// address whose 4-byte word is not fully mapped, matching the kernel's contract (guest_mem.rs):
/// `read_u32` never panics on an unmapped, partial or wrapping address.
#[derive(Clone, Debug)]
pub struct ProcessGuest32<G> {
    process: G,
}

impl<G: GuestProcess> ProcessGuest32<G> {
    /// Wrap an already-attached process, or a test fake.
    #[must_use]
    pub fn new(process: G) -> Self {
        Self { process }
    }

    /// The wrapped process.
    #[must_use]
    pub fn process(&self) -> &G {
        &self.process
    }

    fn read_word(&self, addr: u32) -> Option<u32> {
        // Guard the wrapping edge the kernel contract calls out: addr + 3 must not overflow.
        addr.checked_add(3)?;
        let mut buf = [0u8; 4];
        self.process.read_guest(addr, &mut buf)?;
        Some(u32::from_le_bytes(buf))
    }
}

impl<G: GuestProcess> GuestMem32 for ProcessGuest32<G> {
    fn read_u32(&self, addr: u32) -> Option<u32> {
        self.read_word(addr)
    }
}

impl<G: GuestProcess> GuestMem32Mut for ProcessGuest32<G> {
    fn write_u32(&mut self, addr: u32, value: u32) -> Option<()> {
        addr.checked_add(3)?;
        self.process.write_guest(addr, &value.to_le_bytes())
    }
}
