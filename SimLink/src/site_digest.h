// Deterministic 128-bit dual digest for verifying pinned native binary sites without embedding raw instruction byte arrays.
#pragma once

#include <cstddef>
#include <cstdint>

namespace simlink
{
struct SiteDigest
{
    uint64_t hi;
    uint64_t lo;
};

inline SiteDigest ComputeSiteDigest(const void* data, size_t length)
{
    if (data == nullptr)
    {
        return {0ULL, 0ULL};
    }

    const uint8_t* bytes = static_cast<const uint8_t*>(data);
    uint64_t hi = 0x9E3779B97F4A7C15ULL ^ static_cast<uint64_t>(length);
    uint64_t lo = 0xCBF29CE484222325ULL ^ (static_cast<uint64_t>(length) * 0x00000100000001B3ULL);
    for (size_t i = 0; i < length; ++i)
    {
        const uint8_t b = bytes[i];
        lo ^= b;
        lo *= 0x00000100000001B3ULL;
        hi = (hi + (static_cast<uint64_t>(i) * 0x9E3779B97F4A7C15ULL)) ^ b;
        hi *= 0xBF58476D1CE4E5B9ULL;
        hi ^= hi >> 29;
    }

    hi ^= hi >> 31;
    hi *= 0x94D049BB133111EBULL;
    hi ^= hi >> 31;
    return {hi, lo};
}

inline bool SiteDigestMatchesBytes(const void* data, size_t length, const SiteDigest& expected)
{
    if (data == nullptr)
    {
        return false;
    }

    const SiteDigest got = ComputeSiteDigest(data, length);
    return got.hi == expected.hi && got.lo == expected.lo;
}

inline bool SiteDigestMatches(uintptr_t va, size_t length, const SiteDigest& expected)
{
    if (va == 0)
    {
        return false;
    }

    return SiteDigestMatchesBytes(reinterpret_cast<const void*>(va), length, expected);
}
}  // namespace simlink
