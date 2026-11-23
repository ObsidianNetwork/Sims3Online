// Cleanroom phase-0 pending-set fallback pick helper (sacs_patch.cpp; tested by selftest.cpp).
// Pure C++ implementation with no inline assembly or game memory globals.
#pragma once

#include <cstdint>

namespace simlink
{
namespace sacs
{
// Returns the first node in the pending set's bucket array (+0x04 buckets pointer, +0x08 bucket count),
// taking entry[0] of the first non-null bucket, or 0 if empty or malformed.
inline uintptr_t PickFallback(const unsigned char* set)
{
    if (set == nullptr)
    {
        return 0;
    }

    const uintptr_t* buckets = *reinterpret_cast<const uintptr_t* const*>(set + 4);
    const uint32_t count = *reinterpret_cast<const uint32_t*>(set + 8);
    if (buckets == nullptr || count == 0)
    {
        return 0;
    }

    for (uint32_t b = 0; b < count; ++b)
    {
        const uintptr_t entryPtr = buckets[b];
        if (entryPtr != 0)
        {
            const uintptr_t* entry = reinterpret_cast<const uintptr_t*>(entryPtr);
            return entry[0];
        }
    }

    return 0;
}
}  // namespace sacs
}  // namespace simlink
