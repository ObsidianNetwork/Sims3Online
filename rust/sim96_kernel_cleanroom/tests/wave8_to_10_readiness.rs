#![forbid(unsafe_code)]
//! Cards 37/40/44/47/50 readiness — the pure kernel half of the Wave 8-10
//! rehearsal. The daemon-over-TCP cells live in
//! `src/bin/wave8_alpha_readiness_probe.rs`; nothing here opens a socket or
//! touches a game process.
use sim96_kernel_cleanroom::continuity::run_continuity_check;
use sim96_kernel_cleanroom::go_nogo::{
    FULL_GO_UNREACHABLE_AT_WAVE_6, LEDGER_ROW_COUNT, OverallGo, ShipDecision, grade_subsystem, overall_go,
};
use sim96_kernel_cleanroom::integration::run_park_meet;
use sim96_kernel_cleanroom::p7_benchmark::P7Benchmark;
use sim96_kernel_cleanroom::soak_chaos::{
    SOAK_MAX_GEN_HANDLES, SOAK_MAX_QUEUE_PER_SIM, SOAK_MIN_TICKS_PER_SECOND, ref_soak,
};
use sim96_kernel_cleanroom::wan_transport::{
    BRIDGE_PROTOCOL_VERSION, GAP_NACK_COOLDOWN_TICKS, REORDER_WINDOW_FRAMES, RING_BUFFER_FRAMES,
    Resume, SIMLINK_NATIVE_VERSION, ServerRingBuffer, StreamFrameRef, WanIngest, WanJitterBuffer,
};
use sim96_kernel_cleanroom::world_integration::{IntegrationOutcome, WEEK_TICKS, run_full_world};

fn frame(stream: u32, seq: u64) -> StreamFrameRef { StreamFrameRef { stream_id: stream, epoch: 1, seq } }

#[test]
fn wave8_card37_transport_invariants_hold() {
    assert_eq!((REORDER_WINDOW_FRAMES, RING_BUFFER_FRAMES, GAP_NACK_COOLDOWN_TICKS), (32, 2048, 6));
    assert_eq!((BRIDGE_PROTOCOL_VERSION, SIMLINK_NATIVE_VERSION), (17, 15));
    let mut b = WanJitterBuffer::default();
    for s in 1..=40u64 { b.ingest(frame(1, s), vec![0u8], s); }
    for s in 42..=60u64 { assert_eq!(b.ingest(frame(1, s), vec![0u8], s), WanIngest::Buffered); }
    assert_eq!(b.ingest(frame(1, 41), vec![0u8], 41), WanIngest::Delivered);
    assert_eq!(b.delivered, 60);
    assert!(b.window.is_empty() && b.gaps.is_empty());
    let mut ring = ServerRingBuffer::default();
    for s in 1..=3_000u64 { ring.append(frame(2, s), vec![0u8; 4]); }
    assert!(ring.total_frames() <= RING_BUFFER_FRAMES as usize);
    assert!(matches!(ring.resume(2, 1, 2_500), Resume::Ok { frames } if frames.len() == 500));
    assert_eq!(ring.resume(2, 1, 500), Resume::Fresh);
}

#[test]
fn wave9_card44_models_agree_with_the_live_rehearsal() {
    let IntegrationOutcome::Pass(week) = run_full_world(WEEK_TICKS, 0x5EED) else { panic!("week regressed") };
    assert!(week.all_green());
    let s = ref_soak(200_000, 0xABCD);
    assert!(s.soak_green() && s.max_handles <= SOAK_MAX_GEN_HANDLES
        && s.max_queue <= SOAK_MAX_QUEUE_PER_SIM
        && s.ticks_per_second >= SOAK_MIN_TICKS_PER_SECOND);
    let m = P7Benchmark::measure(1_000);
    assert!(m.within_budget() && m.state_hash_matches_across_seeds);
}

#[test]
fn wave10_card47_continuity_and_checksum() {
    let cont = run_continuity_check(0x5EED, 500, 1_000);
    assert!(cont.bit_identical, "diverged at {:?}", cont.first_diverging_field);
    let day = run_continuity_check(7, 500, 54_000);
    assert!(day.bit_identical);
}

#[test]
fn wave10_card50_verdict_is_selective_go() {
    let d = grade_subsystem(30, 30, 0, 0, 10, 2, true, true, true);
    assert_eq!(d, ShipDecision::ShipAsSimlinkAuthoritySeam);
    let modules = [("routing", d), ("wire", d)];
    assert_eq!(overall_go(&modules), OverallGo::SelectiveGo);
    assert_eq!(LEDGER_ROW_COUNT, 12);
    const { assert!(FULL_GO_UNREACHABLE_AT_WAVE_6); }
}

#[test]
fn wave9_card40_park_meet_green() {
    let outcome = run_park_meet(1_000, 7).expect("park meet rejected");
    assert!(outcome.stats.benchmark_green() && outcome.stats.pairs_transitioned == 3);
}
