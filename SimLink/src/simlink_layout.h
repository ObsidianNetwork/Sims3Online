// SimLink shared-memory layout, version 1. The .NET reader (DedicatedServer/HostBridge/SimLinkShadowReader.cs)
// mirrors these offsets; change both together and bump kLayoutVersion.
//
// Section "Local\SimsOnline.SimLink.<pid>" (a TS3 in a Sandboxie box: the controller opens
// \Sandbox\<user>\<box>\Session_<n>\Sessions\<n>\BaseNamedObjects\SimsOnline.SimLink.<pid> by NT path).
// Events "<section>.control" (controller -> sidecar) and "<section>.data" (sidecar -> controller), auto-reset.
//
//   [0, kHeaderSize)              header (fields below, little-endian)
//   [kHeaderSize, +ringSize)      one single-producer/single-consumer byte ring (the presentation lane in v1)
//
// Records are 8-byte aligned: u32 size (whole record incl. padding), u16 kind, u16 lane, i32 seq, u32 chars,
// i64 qpc (QueryPerformanceCounter at publish), then chars x UTF-16. A Pad record (kind 2) fills the space to the
// ring's end when a record doesn't fit there; readers skip it by its size (only its first 8 bytes are written).
// writePos/readPos are monotonically increasing byte counts; the producer owns writePos, the consumer readPos.
#pragma once

#include <cstdint>

namespace simlink
{
constexpr char kMagic[8] = {'S', 'I', 'M', 'L', 'I', 'N', 'K', '1'};
constexpr uint32_t kLayoutVersion = 1;
constexpr uint32_t kHeaderSize = 256;
constexpr uint32_t kDefaultRingSize = 4u << 20;  // 4 MB, a power of two
constexpr uint32_t kRecordHeader = 24;
constexpr int32_t kNativeVersion = 15;  // 2: SacsReadSeed / SacsWriteSeeds ( tier 1); 3: tier 3 picks;
                                       // 4: site A' order log; 5: item 3 keyed Random draw (SacsRandomCounter);
                                       // 6: item 3's input log (SacsRandomCount, SacsRandomWord);
                                       // 7: GC rate gate (SacsGcCounter, SacsGcInterval);
                                       // 8: explicit-free ring (SacsFreeRing);
                                       // 9: the ring's layout 2, each free's thread (SacsFreeRing 6-8);
                                       // 10: chunk reserve (SacsChunkReserve);
                                       // 11: the chunk reserve on by default (SIMLINK_CHUNKRESERVE=0 turns it off);
                                       // 12: on-screen walk (SacsOnScreen; SIMLINK_ONSCREEN=0 turns it off);
                                       // 13: walker counters copied (SacsWalkerRead/Word/Write/Carry/Counter);
                                       // 14: the GC heap lever (SacsGcHeap; SIMLINK_GCHEAP, SIMLINK_GCFORCED, both opt-in)
                                       // 15: the GC heap lever on by default (352,32; SIMLINK_GCHEAP=0 off)

enum State : uint32_t
{
    StateCreated = 0,
    StateRegisterRequested = 1,  // written by the controller, then it signals .control
    StateRegistered = 2,         // internal calls registered: the controller may send SACS_RECORDER sidecar-on
    StateFailed = 3,             // see Error
};

enum Error : uint32_t
{
    ErrorNone = 0,
    ErrorRegistrarBytes = 1,  // mono_add_internal_call at kRegistrarVa doesn't match this TS3W.exe build
    ErrorIcallTableNull = 2,  // Mono's internal-call table isn't created yet
    ErrorException = 3,       // structured exception while registering
};

enum RecordKind : uint16_t
{
    KindData = 1,
    KindPad = 2,
};

namespace off
{
constexpr uint32_t Magic = 0;               // char[8]
constexpr uint32_t LayoutVersion = 8;       // u32
constexpr uint32_t HeaderSize = 12;         // u32
constexpr uint32_t Pid = 16;                // u32
constexpr uint32_t State = 20;              // u32, see State
constexpr uint32_t Error = 24;              // u32, see Error
constexpr uint32_t RingOffset = 28;         // u32
constexpr uint32_t RingSize = 32;           // u32, power of two
constexpr uint32_t StringLengthOffset = 36; // u32, offset of a managed string's length (chars follow 4 bytes on)
constexpr uint32_t QpcFrequency = 40;       // i64
constexpr uint32_t WritePos = 48;           // u64, producer
constexpr uint32_t ReadPos = 56;            // u64, consumer
constexpr uint32_t Published = 64;          // u64
constexpr uint32_t Dropped = 72;            // u64, ring full or record too large
constexpr uint32_t Contended = 80;          // u64, a second writer while one was publishing
constexpr uint32_t BadArgs = 88;            // u64, null string, wrong length, layout not probed
constexpr uint32_t NativeVersion = 96;      // i32
constexpr uint32_t ProbeResult = 100;       // i32, string-layout probe: offset found, or <0
constexpr uint32_t PatchState = 104;        // i32, tier 3 code patches: state | detail << 8 (sacs_patch.h)
constexpr uint32_t Build = 128;             // char[64], build description
}  // namespace off
}  // namespace simlink
