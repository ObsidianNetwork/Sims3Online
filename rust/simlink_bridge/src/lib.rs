//! `simlink_bridge`: the layout-v11 process-memory bridge in Rust ( card 56,
//! `docs/engine/sim96_specs/56_simlink_bridge_process_memory_rust.md`).
//!
//! It attaches to a running server TS3W, finds the shared region by its 32-byte marker, and exchanges the
//! bridge verbs with the server mod byte for byte as the C# `ProcessMemoryBridge` does. The C# controller
//! stays the oracle and the running path until card 60.
//!
//! Every platform call sits in [`ffi`] behind a safe API (card 56 §D, `AUDIT_UNSAFE.md`). This crate root
//! denies the lint crate-wide and allows it for `ffi` alone; every other source file also forbids it.
//! It does not depend on `sim96_kernel_cleanroom` (card 56 §A), and card 57's daemon takes both crates.
#![deny(unsafe_code)]

pub mod corpus;
pub mod crc32;
pub mod discovery;
pub mod driver;
pub mod error;
#[allow(unsafe_code)]
pub mod ffi;
pub mod layout;
pub mod live;
pub mod memory;
pub mod record;
pub mod ring;
pub mod reply;
pub mod scorecard;
pub mod text;
pub mod verbs;

pub use driver::{discover, BridgeDriver, BulkPayload, HelloReply, Pacer, SleepPacer};
pub use error::BridgeError;
pub use ffi::ProcessAttach;
pub use memory::{GuestMemory, RegionInfo, SimMemory};
pub use verbs::{encode, BridgeCommand, VERB_TABLE};
