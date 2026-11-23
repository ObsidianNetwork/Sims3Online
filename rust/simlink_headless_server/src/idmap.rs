#![forbid(unsafe_code)]
//! The wire `IdMap` (§E).
//!
//! The public wire ids are u64 end to end; the kernel's `SimId` is a `u32` (`lib.rs:63`). The
//! C# sidecar's `unchecked((uint)objectId)` narrowing is NOT reproduced: the front door keeps an
//! injective, explicit map from the public u64 ids to the kernel's u32 `SimId`, assigned in
//! first-seen order, with `SERVER_PRIMARY_SIM` reserved and never a truncation. A full map is the
//! refusal `IdSpaceFull`. Every kernel id in a reply is mapped back before it leaves the process.
//! This is the one place a wider kernel id lands (§J.6).

use std::collections::HashMap;

use sim96_kernel_cleanroom::runtime_bridge::SERVER_PRIMARY_SIM;
use sim96_kernel_cleanroom::SimId;

use crate::messages::RefusalCode;

/// A bijection between public u64 ids and kernel u32 `SimId`s, allocated in first-seen order.
#[derive(Clone, Debug, Default)]
pub struct IdMap {
    wire_to_kernel: HashMap<u64, SimId>,
    kernel_to_wire: HashMap<SimId, u64>,
    /// The next kernel id to hand out. `SERVER_PRIMARY_SIM` is skipped so it is never assigned
    /// to a wire id.
    next_kernel: u32,
}

impl IdMap {
    /// A fresh map starting allocation at 0 and reserving [`SERVER_PRIMARY_SIM`].
    #[must_use]
    pub fn new() -> Self {
        Self::starting_at(0)
    }

    /// A map whose allocation counter starts at `next`, for tests that drive it to exhaustion.
    #[must_use]
    pub fn starting_at(next: u32) -> Self {
        Self {
            wire_to_kernel: HashMap::new(),
            kernel_to_wire: HashMap::new(),
            next_kernel: next,
        }
    }

    /// The kernel id for a public id, assigning the next free kernel id on first sight (§E).
    /// Returns [`RefusalCode::IdSpaceFull`] when the u32 id space is exhausted.
    pub fn map_wire(&mut self, wire: u64) -> Result<SimId, RefusalCode> {
        if let Some(&kernel) = self.wire_to_kernel.get(&wire) {
            return Ok(kernel);
        }
        let kernel = self.take_next_kernel()?;
        self.wire_to_kernel.insert(wire, kernel);
        self.kernel_to_wire.insert(kernel, wire);
        Ok(kernel)
    }

    /// The public id a kernel id maps back to, for a reply leaving the process (§E).
    #[must_use]
    pub fn kernel_to_wire(&self, kernel: SimId) -> Option<u64> {
        self.kernel_to_wire.get(&kernel).copied()
    }

    /// How many ids are mapped.
    #[must_use]
    pub fn len(&self) -> usize {
        self.wire_to_kernel.len()
    }

    /// Whether no ids are mapped yet.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.wire_to_kernel.is_empty()
    }

    fn take_next_kernel(&mut self) -> Result<SimId, RefusalCode> {
        // Skip the reserved primary sim id.
        if self.next_kernel == SERVER_PRIMARY_SIM.0 {
            self.next_kernel = self.next_kernel.checked_add(1).ok_or(RefusalCode::IdSpaceFull)?;
        }
        let kernel = SimId(self.next_kernel);
        self.next_kernel = self.next_kernel.checked_add(1).ok_or(RefusalCode::IdSpaceFull)?;
        Ok(kernel)
    }
}
