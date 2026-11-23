// the explicit-free ring's layout and its pure logic. free_ring.cpp records into it from a hook on GC_free's
// entry; selftest.cpp (`simlink_selftest freering`) tests this file. Nothing here reads game memory.
//
// The ring is one static, 64-byte-aligned struct in SimLink.asi's .bss, exported as SimLink_FreeRing: a reader finds it
// at SimLink.asi's base + that export's RVA (the export table of the .asi on disk), or by its magic. TS3's .mdmp holds
// no heap or .bss, so a dump read needs a full dump. Layout version 2 (native version 9), little-endian. A reader takes
// the capacity, the entry size and the entries' offset from the header, never constants:
//
//   header, 64 bytes at +0:
//     +0  magic "SLFREE1\0" (every layout version)
//     +8  layout version (2; layout 1, native version 8, had 40-byte entries without the thread, and no script thread)
//     +12 entry size (44)
//     +16 capacity (131072, a power of two)
//     +20 head: frees recorded so far. Entry (head - 1) % capacity is the newest.
//     +24 enabled: 1 while recording (0 before install, and after a failed install)
//     +28 the thread that installed the hook: DllMain's at load, not the thread that runs scripts
//     +32 entries offset (64)
//     +36 the script thread: the thread of the first frame-release free (fields 1-5 and 7), 0 until one
//     +40..+63 reserved (0)
//   entries, 44 bytes each at +64 + index * 44:
//     +0  seq: the free's 1-based number, written last. An entry is whole only when seq is in
//         (head - capacity, head] and (seq - 1) % capacity is its index; anything else is stale or half-written.
//     +4  block: the pointer freed (GC_free's argument; 0 is never recorded)
//     +8  size in words, from the block's header (*hdr); 0 when the header could not be read
//     +12 the block's word 0 before the free (an object's vtable; GC_free overwrites it with the free-list link)
//     +16 caller: [esp] at GC_free's entry, the return address (a thunk caller's, through 0xD71810)
//     +20 caller2: [esp+8], the wrapper's own caller for 0xD70DCA and 0xD714CA (meaningless elsewhere)
//     +24 frame: the interpreter frame the free belongs to (kFreeSites says where it comes from), 0 when none
//     +28 GC_gc_no (0x011F2F90) at the free
//     +32 GetTickCount at the free, ms
//     +36 kind: the header's +0x14 byte
//     +37 the frame's +0x148 byte (it owns its chunks and is freed with them)
//     +38 the frame's +0x14b byte (borrowed: made on the C stack)
//     +39 field: a Field code, | 0x80 when the free ran on another thread than the script thread (header +36). A
//         free before the script thread is known is never flagged; its thread (+40) still says.
//     +40 the thread that ran the free (GetCurrentThreadId)
#pragma once

#include <intrin.h>

#include <cstddef>
#include <cstdint>

namespace simlink
{
namespace freering
{
constexpr uint32_t kLayoutVersion = 2;
// A power of two (Slot masks). 131072 entries are 5.8 MB of .bss: at about 10.8k frees a second (D's catcher arms),
// 65536 entries covered 5.6-7.5 s, and the ring has to reach back past the last collection before a crash.
constexpr uint32_t kCapacity = 131072;
constexpr char kMagic[8] = {'S', 'L', 'F', 'R', 'E', 'E', '1', '\0'};
constexpr uint8_t kOffScriptThread = 0x80;

// What a free releases, by its caller (the return address at GC_free's entry).
enum Field : uint8_t
{
    FieldOther = 0,               // a caller not in kFreeSites
    FieldFrameList = 1,           // 0xE52DD0 frame release: an entry of the frame's +0x3c list
    FieldFrameInvocation = 2,     // 0xE52DD0: +0x30, the invocation record
    FieldFrameArguments = 3,      // 0xE52DD0: +0x2c, the argument array
    FieldFrameReturnSlot = 4,     // 0xE52DD0: +0x38, the return slot
    FieldFrameThreadContext = 5,  // 0xE52DD0: +0x14, the thread context
    FieldFrameChunk = 6,          // 0xD71B70: a stack chunk of the frame (frame release, SetTaskContext)
    FieldFrame = 7,               // 0xE53E80: the 0x150-byte frame itself, after a memset
    FieldExitChunk = 8,           // 0xD71A20: a stack chunk popped at method exit
    FieldFrameListReset = 9,      // 0xE535E0: an entry of the frame's +0x3c list (reached from 0xE5404D)
    FieldSetTaskContext = 10,     // 0xD714C0, from ICALL_TaskControl_SetTaskContext (caller2: its caller)
    FieldAllocatorFree = 11,      // 0xD70DC0, the Free slot of an engine allocator vtable (caller2: the engine caller)
    FieldShutdown = 12,           // ShutdownScriptHostTasks
    FieldBoehm = 13,              // Boehm's own calls
};

// Where the frame of a free comes from: EBP at GC_free's entry (0xE52DD0 keeps it there), the block itself (the frame's
// own free), or a dword of GC_free's entry stack (the caller's argument, past its pushes and return address).
enum FrameSource : uint8_t
{
    FrameNone = 0,
    FrameEbp = 1,
    FrameBlock = 2,
    FrameStack = 3,
};

struct FreeSite
{
    uint32_t ret;         // the return address of a call to GC_free (0xE4BEB0) or its thunk (0xD71810)
    uint8_t field;        // Field
    uint8_t source;       // FrameSource
    uint8_t stackOffset;  // FrameStack: the frame's offset from GC_free's entry ESP, in bytes
};

// Every call to GC_free in Steam 1.67.2.024037 TS3W.exe (00e4beb0 and thunk 00d71810): 15 calls and the thunk's 3.
constexpr FreeSite kFreeSites[] = {
    {0x00E52DF8, FieldFrameList, FrameEbp, 0},
    {0x00E52E26, FieldFrameInvocation, FrameEbp, 0},
    {0x00E52E39, FieldFrameArguments, FrameEbp, 0},
    {0x00E52E4C, FieldFrameReturnSlot, FrameEbp, 0},
    {0x00E52E5F, FieldFrameThreadContext, FrameEbp, 0},
    {0x00D71BCB, FieldFrameChunk, FrameStack, 20},     // pushes esi, edi, then its argument
    {0x00E53EAA, FieldFrame, FrameBlock, 0},
    {0x00D71AB0, FieldExitChunk, FrameStack, 24},      // pushes esi, ebp, edi
    {0x00D71B4C, FieldExitChunk, FrameStack, 28},      // pushes esi, ebp, edi, ebx
    {0x00E53605, FieldFrameListReset, FrameStack, 28}, // pushes ebx, ebp, edi, esi (EBX holds it too)
    {0x00D714CA, FieldSetTaskContext, FrameNone, 0},
    {0x00D70DCA, FieldAllocatorFree, FrameNone, 0},
    {0x00D80AD8, FieldShutdown, FrameNone, 0},
    {0x00D80B0F, FieldShutdown, FrameNone, 0},
    {0x00D80B3F, FieldShutdown, FrameNone, 0},
    {0x00D80B52, FieldShutdown, FrameNone, 0},
    {0x00E4AC51, FieldBoehm, FrameNone, 0},
    {0x00E4F364, FieldBoehm, FrameNone, 0},
};

struct Entry
{
    uint32_t seq;
    uint32_t block;
    uint32_t sizeWords;
    uint32_t word0;
    uint32_t caller;
    uint32_t caller2;
    uint32_t frame;
    uint32_t gcNo;
    uint32_t tickMs;
    uint8_t kind;
    uint8_t flags148;
    uint8_t flags14b;
    uint8_t field;
    uint32_t tid;
};

struct Header
{
    char magic[8];
    uint32_t version;
    uint32_t entrySize;
    uint32_t capacity;
    volatile long head;
    uint32_t enabled;
    uint32_t installThread;
    uint32_t entriesOffset;
    volatile long scriptThread;
    uint32_t reserved[6];
};

struct alignas(64) Ring
{
    Header header;
    Entry entries[kCapacity];
};

static_assert(sizeof(Entry) == 44, "the dump layout: 44-byte entries");
static_assert(sizeof(Header) == 64, "the dump layout: a 64-byte header");
static_assert(offsetof(Header, head) == 20 && offsetof(Header, enabled) == 24 && offsetof(Header, installThread) == 28 &&
                  offsetof(Header, entriesOffset) == 32 && offsetof(Header, scriptThread) == 36,
              "the dump layout: header fields");
static_assert(offsetof(Entry, frame) == 24 && offsetof(Entry, kind) == 36 && offsetof(Entry, field) == 39 &&
                  offsetof(Entry, tid) == 40,
              "the dump layout: entry fields");
static_assert(offsetof(Ring, entries) == 64, "the dump layout: entries at +64");
static_assert((kCapacity & (kCapacity - 1)) == 0, "Slot masks: a power-of-two capacity");
static_assert(sizeof(Ring) == 64 + sizeof(Entry) * kCapacity, "the dump layout: the header, then capacity entries");

inline const FreeSite* SiteFor(uint32_t ret)
{
    for (const FreeSite& site : kFreeSites)
    {
        if (site.ret == ret)
        {
            return &site;
        }
    }

    return nullptr;
}

// The frame of a free from `site`. `stack` is GC_free's entry ESP: [0] the return address, [1] the block.
inline uint32_t FrameFor(const FreeSite* site, uint32_t ebp, uint32_t block, const uint32_t* stack)
{
    if (site == nullptr)
    {
        return 0;
    }

    switch (site->source)
    {
    case FrameEbp:
        return ebp;
    case FrameBlock:
        return block;
    case FrameStack:
        return stack[site->stackOffset / 4];
    default:
        return 0;
    }
}

inline uint32_t Slot(uint32_t seq)
{
    return (seq - 1) & (kCapacity - 1);
}

// Writes `e` as free number `seq`, its seq last, so a reader can tell a half-written entry.
inline void Write(Ring& ring, uint32_t seq, const Entry& e)
{
    Entry& slot = ring.entries[Slot(seq)];
    *reinterpret_cast<volatile uint32_t*>(&slot.seq) = 0;
    slot.block = e.block;
    slot.sizeWords = e.sizeWords;
    slot.word0 = e.word0;
    slot.caller = e.caller;
    slot.caller2 = e.caller2;
    slot.frame = e.frame;
    slot.gcNo = e.gcNo;
    slot.tickMs = e.tickMs;
    slot.kind = e.kind;
    slot.flags148 = e.flags148;
    slot.flags14b = e.flags14b;
    slot.field = e.field;
    slot.tid = e.tid;
    *reinterpret_cast<volatile uint32_t*>(&slot.seq) = seq;
}

// The interpreter's frame release (0xE52DD0, fields 1-5) and the frame's own free (7) run on the thread that runs
// scripts: the first of them names it.
inline bool IsFrameRelease(uint8_t field)
{
    return (field >= FieldFrameList && field <= FieldFrameThreadContext) || field == FieldFrame;
}

// The 0x80 flag for a free with `field` (no flag bits) on thread `tid`. The first frame-release free claims the script
// thread (header +36); from then on a free on another thread is flagged. 0 while the script thread is unknown.
inline uint8_t ThreadFlag(volatile long& scriptThread, uint8_t field, uint32_t tid)
{
    if (scriptThread == 0 && IsFrameRelease(field))
    {
        _InterlockedCompareExchange(&scriptThread, static_cast<long>(tid), 0);
    }

    const uint32_t script = static_cast<uint32_t>(scriptThread);
    return script != 0 && script != tid ? kOffScriptThread : 0;
}

// The last frame whose flags were read: 0xE53E80 zeroes the frame before freeing it, so its own entry takes the
// flags of the release just before.
struct LastFrame
{
    uint32_t frame;
    uint8_t flags148;
    uint8_t flags14b;
};

// Frees per second over the last window of at least a second.
struct Rate
{
    uint32_t windowTick;
    uint32_t windowSeq;
    uint32_t perSec;
};

inline void Tick(Rate& rate, uint32_t seq, uint32_t tickMs)
{
    const uint32_t elapsed = tickMs - rate.windowTick;
    if (elapsed >= 1000)
    {
        rate.perSec = static_cast<uint32_t>(static_cast<uint64_t>(seq - rate.windowSeq) * 1000u / elapsed);
        rate.windowSeq = seq;
        rate.windowTick = tickMs;
    }
}
}  // namespace freering
}  // namespace simlink
