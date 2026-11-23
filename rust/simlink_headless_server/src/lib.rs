#![forbid(unsafe_code)]
//! `simlink_headless_server`: the Rust headless server binary and the public line protocol v1
//! (card 57, [`wire_v1`]).
//!
//! One Rust process owns the public socket, sessions, claims, ordering and idempotency,
//! adjudicates through `sim96_kernel_cleanroom`, and drives the server TS3W through card 56's
//! `simlink_bridge` (card 57 §A). The C# `DedicatedServer` stays the oracle and the running path
//! until card 60; this crate changes no behaviour on that path (front door §K).
//!
//! `unsafe` stays in `simlink_bridge/src/ffi.rs`; this crate forbids it in every file. It takes
//! both crates as path dependencies; the kernel itself never depends on the bridge (§J).
//!
//! The codec is one module ([`wire_v1`]) behind the message types ([`messages`]), and the
//! transport is one module behind the session, so a QUIC listener can land there later (§J.6).

pub mod bind;
pub mod checkpoint;
pub mod world_save;
pub mod join_reuse;
pub mod last_good;
pub mod rejoin;
pub mod card59_scorecard;
pub mod admin;
pub mod cadence;
pub mod presentation;
pub mod routing;
pub mod world_time;
pub mod snapshot;
pub mod stream;
pub mod events;
pub mod trace;
pub mod card58_scorecard;
pub mod bridge_driver;
pub mod nav_sync;
pub mod error;
pub mod guest_mem32;
pub mod idmap;
pub mod messages;
pub mod session;
pub mod serve;
pub mod scorecard;
pub mod transport;
pub mod wire_v1;

pub use bind::{
    bind_all, parse_endpoint, posture_from_args, unrecognized_args, BindError, BindPosture,
    Listener,
};
pub use error::WireError;
pub use guest_mem32::{GuestProcess, ProcessGuest32};
pub use idmap::IdMap;
pub use session::{CachedReply, CommandOutcome, ConnId, Connection, Refusal, Server};
pub use bridge_driver::BridgeGameDriver;
pub use transport::{DriverError, FakeDriver, GameDriver};
pub use wire_v1::{
    b64_decode, b64_encode, decode_ack, decode_bulk, decode_command, decode_err, decode_event,
    decode_hello, decode_menu_refusal, decode_welcome, encode_ack, encode_bulk, encode_command,
    encode_err, encode_event, encode_hello, encode_menu_refusal, encode_welcome, judge_hello,
    line, split, terminate, BulkReceiver, HelloRefusal, HelloToken,
};
