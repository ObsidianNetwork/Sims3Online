// the chunk reserve's byte replacements. chunk_reserve.cpp writes them at load (on by default from native
// version 11; SIMLINK_CHUNKRESERVE=0 turns it off); selftest.cpp (`simlink_selftest chunkreserve`) checks the table and
// the default. Nothing here reads game memory.
//
// The interpreter (Mono's mint) carves each method's frame (args, eval stack, valuetypes, locals) from a stack chunk:
// a 0x800-byte typed GC block, +0 top, +4 end (chunk + 0x7fc), +8 the older chunk, data from +0xc. The block's typed
// GC descriptor is its last word, +0x7fc, which is `end` itself. A frame that ends exactly at `end` gets a store up to
// 4 bytes past its last slot, which zeroes the descriptor (the best candidate is a 12-byte
// valuetype return into an 8-byte slot). Boehm then scans nothing in the chunk, not even its link to older
// chunks. Mid-chunk the same store lands above top and does no harm.
//
// The reserve keeps every frame off `end`: both fit tests take a frame only when it leaves room (end - top > size,
// was >=), and a new chunk is sized for the frame + 0x10 (was + 0xc). So a pool chunk takes frames up to 0x7ec, and a
// chunk sized for one large frame keeps 4 bytes of slack. These are the only places that carve frames.
// Each row is a whole instruction window, so its check covers the comparison it changes.
#pragma once

#include <cstddef>
#include <cstdint>

namespace simlink
{
namespace chunkreserve
{
struct ByteReplace
{
    uintptr_t va;
    const unsigned char* original;     // checked before anything is written
    const unsigned char* replacement;  // the same length
    size_t length;
};

// 0xE563BB, the inline fit test at method entry: room (end - top) against the frame size (the method's +0x38).
// cmp ecx, edx / jl 0xe563d5 (the slow path) becomes cmp ecx, edx / jle 0xe563d5.
constexpr unsigned char kInlineFit[] = {0x3B, 0xCA, 0x7C, 0x16};
constexpr unsigned char kInlineFitReserve[] = {0x3B, 0xCA, 0x7E, 0x16};

// 0xD71919, slow-path fit test: cmp ecx, ebp / jge 0xd71a0d (fits) becomes cmp ecx, ebp / jg 0xd71a0d.
constexpr unsigned char kSlowFit[] = {0x3B, 0xCD, 0x0F, 0x8D, 0xEC, 0x00, 0x00, 0x00};
constexpr unsigned char kSlowFitReserve[] = {0x3B, 0xCD, 0x0F, 0x8F, 0xEC, 0x00, 0x00, 0x00};

// 0xD71946, slow-path new chunk size, then a pool chunk when it is at most 0x7fc:
// lea ecx, [ebp + 0xc] / mov [esp + 0x10], ecx / cmp ecx, 0x7fc becomes lea ecx, [ebp + 0x10] / ...
constexpr unsigned char kChunkSize[] = {0x8D, 0x4D, 0x0C, 0x89, 0x4C, 0x24, 0x10, 0x81, 0xF9, 0xFC, 0x07, 0x00, 0x00};
constexpr unsigned char kChunkSizeReserve[] = {0x8D, 0x4D, 0x10, 0x89, 0x4C, 0x24, 0x10, 0x81, 0xF9, 0xFC, 0x07, 0x00,
                                               0x00};

constexpr ByteReplace kReserveReplaces[] = {
    {0x00E563BB, kInlineFit, kInlineFitReserve, sizeof(kInlineFit)},
    {0x00D71919, kSlowFit, kSlowFitReserve, sizeof(kSlowFit)},
    {0x00D71946, kChunkSize, kChunkSizeReserve, sizeof(kChunkSize)},
};
constexpr size_t kReserveCount = sizeof(kReserveReplaces) / sizeof(kReserveReplaces[0]);

static_assert(sizeof(kInlineFit) == sizeof(kInlineFitReserve) && sizeof(kSlowFit) == sizeof(kSlowFitReserve) &&
                  sizeof(kChunkSize) == sizeof(kChunkSizeReserve),
              "each replacement has its original's length");

// On by default (native version 11): only SIMLINK_CHUNKRESERVE=0 turns it off. `length` is what GetEnvironmentVariableW
// returned for the value: 0 when the variable is not set or empty, the size it needs when the buffer is too small.
inline bool ReserveWanted(const wchar_t* value, unsigned long length)
{
    return !(length == 1 && value[0] == L'0');
}

// A pool chunk's room (end - (chunk + 0xc)) and the largest frame it takes with the reserve.
constexpr uint32_t kPoolRoom = 0x7FC - 0xC;
constexpr uint32_t kPoolFrameMax = 0x7FC - 0x10;
}  // namespace chunkreserve
}  // namespace simlink
