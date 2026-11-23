// The GC heap lever's byte replacements (native version 14; on by default from 15). gc_heap.cpp writes them at
// load: 352 MB and 32 MB when SIMLINK_GCHEAP is unset, SIMLINK_GCHEAP=<initialMB>,<growthMB> for other values,
// nothing with SIMLINK_GCHEAP=0; selftest.cpp (`simlink_selftest gcheap`) checks the table, the
// immediates and the variable's parse. Nothing here reads game memory.
//
// TS3W.exe's Boehm collector starts with a 40 MB heap and grows it 8 MB at a time. EA edited GC_collect_or_expand
// (VA 0xE4A120): in incremental mode, when an allocation finds no room and words were allocated since the last
// collection, it runs a whole collection to its end (the thunk at 0xD70800 to the never-stop collect, VA 0xE4A320)
// instead of growing the heap. The sims3-modernization project measured those forced collections at 3-16 ms per
// second with single pauses of 150-240 ms, and ships a 352 MB initial heap and a 32 MB growth step (their
// docs/re/patch-points.md, CC BY 4.0).
//
// Units, read from the code and not from their table: every immediate here is a count of 4 KB heap blocks.
// - GC_init (VA 0xE4E160) keeps the initial size in ebp (mov ebp, 0x2800: 40 MB) and passes it to
//   GC_expand_hp_inner (0xE49C80), which takes blocks. GC_INITIAL_HEAP_SIZE (bytes, MSVCR80 getenv + atoi), when
//   set, replaces ebp before that call, so it overrides this row: leave it unset in an A/B.
// - GC_collect_or_expand's growth is stock Boehm's: blocks_to_get = GC_heapsize / (4096 * GC_free_space_divisor) +
//   needed (shl ecx, 0xc builds the divisor), clamped to MAXHINCR (0x800 blocks, 8 MB) at three places that must
//   agree: cmp/jbe, cmp/ja (needed + slop, the slop from GC_black_list_spacing >> 12), and mov eax. The result goes
//   to GC_expand_hp_inner. All three rows or none.
//
// Each row is a whole instruction window, so its check covers the jcc after the comparison it changes. The
// replacement arrays are templates at the values the sims3-modernization release ships (352 MB, 32 MB); gc_heap.cpp
// writes each row's original with its immediate (bytes 1-4) set from SIMLINK_GCHEAP.
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

#include "chunk_reserve_logic.h"

namespace simlink
{
namespace gcheap
{
using chunkreserve::ByteReplace;

// 0xE4E168, GC_init's initial heap: mov ebp, 0x2800 (40 MB). Template: mov ebp, 0x16000 (352 MB).
constexpr unsigned char kHeapInitImm[] = {0xBD, 0x00, 0x28, 0x00, 0x00};
constexpr unsigned char kHeapInitImm352[] = {0xBD, 0x00, 0x60, 0x01, 0x00};

// 0xE4A1D8, growth clamp 1/3: cmp eax, 0x800 / jbe 0xe4a20b (push eax: blocks_to_get as it is). Template: 0x2000.
constexpr unsigned char kHeapGrowCmpLe[] = {0x3D, 0x00, 0x08, 0x00, 0x00, 0x76, 0x2C};
constexpr unsigned char kHeapGrowCmpLe32[] = {0x3D, 0x00, 0x20, 0x00, 0x00, 0x76, 0x2C};

// 0xE4A1FF, growth clamp 2/3: cmp eax, 0x800 / ja 0xe4a20b (push eax: needed + slop). Template: 0x2000.
constexpr unsigned char kHeapGrowCmpGt[] = {0x3D, 0x00, 0x08, 0x00, 0x00, 0x77, 0x05};
constexpr unsigned char kHeapGrowCmpGt32[] = {0x3D, 0x00, 0x20, 0x00, 0x00, 0x77, 0x05};

// 0xE4A206, growth clamp 3/3: mov eax, 0x800 / push eax (MAXHINCR itself). Template: 0x2000.
constexpr unsigned char kHeapGrowMov[] = {0xB8, 0x00, 0x08, 0x00, 0x00, 0x50};
constexpr unsigned char kHeapGrowMov32[] = {0xB8, 0x00, 0x20, 0x00, 0x00, 0x50};

constexpr ByteReplace kHeapReplaces[] = {
    {0x00E4E168, kHeapInitImm, kHeapInitImm352, sizeof(kHeapInitImm)},
    {0x00E4A1D8, kHeapGrowCmpLe, kHeapGrowCmpLe32, sizeof(kHeapGrowCmpLe)},
    {0x00E4A1FF, kHeapGrowCmpGt, kHeapGrowCmpGt32, sizeof(kHeapGrowCmpGt)},
    {0x00E4A206, kHeapGrowMov, kHeapGrowMov32, sizeof(kHeapGrowMov)},
};
constexpr size_t kHeapCount = sizeof(kHeapReplaces) / sizeof(kHeapReplaces[0]);
constexpr size_t kHeapRowMax = 8;  // the longest row's length, for the buffer a row is built in
constexpr size_t kImmOffset = 1;   // every row's imm32 follows a one-byte opcode
constexpr bool kRowIsGrowth[kHeapCount] = {false, true, true, true};

static_assert(sizeof(kHeapInitImm) == sizeof(kHeapInitImm352) && sizeof(kHeapGrowCmpLe) == sizeof(kHeapGrowCmpLe32) &&
                  sizeof(kHeapGrowCmpGt) == sizeof(kHeapGrowCmpGt32) && sizeof(kHeapGrowMov) == sizeof(kHeapGrowMov32),
              "each template has its original's length");
static_assert(sizeof(kHeapGrowCmpLe) <= kHeapRowMax && sizeof(kHeapInitImm) <= kHeapRowMax, "rows fit the buffer");

// Stock values in blocks, and the unit.
constexpr uint32_t kBlocksPerMB = 256;  // 4 KB blocks
constexpr uint32_t kStockInitialBlocks = 0x2800;
constexpr uint32_t kStockGrowthBlocks = 0x800;

// SIMLINK_GCHEAP's bounds. Below stock is refused (no reason to shrink either); the initial heap is one contiguous
// reservation at GC_init, and one too large for the address space makes GC_init print "Can't start up" and exit, so
// the ceiling stays well under what a 32-bit game can reserve in one piece.
constexpr uint32_t kInitialMinMB = 40;
constexpr uint32_t kInitialMaxMB = 512;
constexpr uint32_t kGrowthMinMB = 8;
constexpr uint32_t kGrowthMaxMB = 128;

// The default from native version 15, when SIMLINK_GCHEAP is unset or empty: the sims3-modernization release's
// values, which removed forced collections on the server and both clients (see docs/code-reference/simlink.md).
constexpr uint32_t kDefaultInitialMB = 352;
constexpr uint32_t kDefaultGrowthMB = 32;
static_assert(kDefaultInitialMB >= kInitialMinMB && kDefaultInitialMB <= kInitialMaxMB &&
                  kDefaultGrowthMB >= kGrowthMinMB && kDefaultGrowthMB <= kGrowthMaxMB,
              "the default is within the bounds");

enum SpecResult
{
    SpecOff = 0,         // SIMLINK_GCHEAP=0: nothing written
    SpecOk = 1,          // <initialMB>,<growthMB> within the bounds
    SpecMalformed = 2,   // anything else: nothing written
    SpecOutOfRange = 3,  // well formed, outside the bounds: nothing written
    SpecDefault = 4,     // not set or empty: kDefaultInitialMB, kDefaultGrowthMB (native version 15)
};

struct HeapSpec
{
    uint32_t initialMB;
    uint32_t growthMB;
};

// SIMLINK_GCHEAP=<initialMB>,<growthMB>: two decimal numbers of at most 4 digits, one comma, nothing else; exactly
// "0" is off, and not set or empty is the default. `length` is what GetEnvironmentVariableW returned into a buffer
// of `capacity` characters: 0 when not set or empty, the size it needs (>= capacity) when too long.
inline SpecResult ParseSpec(const wchar_t* value, unsigned long length, unsigned long capacity, HeapSpec& out)
{
    out.initialMB = 0;
    out.growthMB = 0;
    if (length == 0)
    {
        out.initialMB = kDefaultInitialMB;
        out.growthMB = kDefaultGrowthMB;
        return SpecDefault;
    }

    if (length >= capacity)
    {
        return SpecMalformed;
    }

    if (length == 1 && value[0] == L'0')
    {
        return SpecOff;
    }

    uint32_t numbers[2] = {0, 0};
    unsigned long at = 0;
    for (int n = 0; n < 2; ++n)
    {
        int digits = 0;
        while (at < length && value[at] >= L'0' && value[at] <= L'9')
        {
            if (++digits > 4)
            {
                return SpecMalformed;
            }

            numbers[n] = numbers[n] * 10u + static_cast<uint32_t>(value[at] - L'0');
            ++at;
        }

        if (digits == 0)
        {
            return SpecMalformed;
        }

        if (n == 0)
        {
            if (at >= length || value[at] != L',')
            {
                return SpecMalformed;
            }

            ++at;
        }
    }

    if (at != length)
    {
        return SpecMalformed;
    }

    if (numbers[0] < kInitialMinMB || numbers[0] > kInitialMaxMB || numbers[1] < kGrowthMinMB ||
        numbers[1] > kGrowthMaxMB)
    {
        return SpecOutOfRange;
    }

    out.initialMB = numbers[0];
    out.growthMB = numbers[1];
    return SpecOk;
}

constexpr uint32_t MBToBlocks(uint32_t mb)
{
    return mb * kBlocksPerMB;
}

// The row's immediate as `bytes` hold it (little-endian imm32 at kImmOffset).
inline uint32_t ImmOf(const unsigned char* bytes)
{
    uint32_t imm = 0;
    std::memcpy(&imm, bytes + kImmOffset, sizeof(imm));
    return imm;
}

// `row`'s original with its immediate set to `blocks`, into `out` (row.length bytes).
inline void BuildRow(const ByteReplace& row, uint32_t blocks, unsigned char* out)
{
    std::memcpy(out, row.original, row.length);
    std::memcpy(out + kImmOffset, &blocks, sizeof(blocks));
}

inline uint32_t RowBlocks(size_t index, const HeapSpec& spec)
{
    return MBToBlocks(kRowIsGrowth[index] ? spec.growthMB : spec.initialMB);
}

// The forced collections' timer is opt-in on its own: SIMLINK_GCFORCED=1 (set in every arm of an A/B, the heap rows
// in one).
inline bool ForcedTimerWanted(const wchar_t* value, unsigned long length)
{
    return length == 1 && value[0] == L'1';
}

// The forced collect thunk at 0xD70800 (jmp 0xE4A320) that the timer retargets, and its target from its rel32.
constexpr uintptr_t kNeverStopCollectAt = 0x00E4A320;
constexpr unsigned char kHeapForcedThunk[] = {0xE9, 0x1B, 0x9B, 0x0D, 0x00};  // jmp 0xE4A320

inline uintptr_t JumpTarget(uintptr_t va, const unsigned char* jmp)
{
    int32_t rel = 0;
    std::memcpy(&rel, jmp + 1, sizeof(rel));
    return va + 5 + static_cast<uintptr_t>(static_cast<intptr_t>(rel));
}

// A forced pause's band edges, for counts an A/B can difference per arm (the max runs from load).
constexpr uint32_t kLongPauseUs = 50000;
constexpr uint32_t kVeryLongPauseUs = 100000;
}  // namespace gcheap
}  // namespace simlink
