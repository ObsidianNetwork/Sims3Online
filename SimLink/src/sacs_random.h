// item 3: the keyed Random-node draw's key and value, kept free of Windows and game memory so the self-test
// (simlink_selftest keyed) and the Python reference (scripts/sacs_random.py) check the same numbers.
//
// The key mixes seven full 32-bit fields with SplitMix64's output function, one step per field, each step adding the
// field and a per-position multiple of SplitMix64's gamma. Nothing is packed or truncated, so no two fields share bits.
// The value is the key's top 24 bits as a float in [0, 1): exactly representable, so the caller's `u * total` rounds
// the same way on every game whatever the x87 precision control.
#pragma once

#include <cstdint>

namespace simlink
{
namespace sacs
{
constexpr uint64_t kRandomDomain = 0x53494D3136355233ull;  // "SIM165R3"
constexpr uint64_t kSplitMixGamma = 0x9E3779B97F4A7C15ull;

inline uint64_t SplitMixOut(uint64_t z)
{
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

// spare: the driver's spare seed (+0x34, copied by tier 1); walker: the walker's index on its driver; graph: the A'
// graph ID; node: the node's A' key (its list-1 position + 1); boundary: the walker's requests and EnterStates since
// its spare took its value; evaluation: this node's evaluations by this walker since that boundary; retry: draws
// before this one in the same evaluation (the no-repeat retry).
inline uint64_t RandomKey(uint32_t spare, uint32_t walker, uint32_t graph, uint32_t node, uint32_t boundary,
                          uint32_t evaluation, uint32_t retry)
{
    const uint32_t fields[7] = {spare, walker, graph, node, boundary, evaluation, retry};
    uint64_t h = kRandomDomain;
    for (uint32_t i = 0; i < 7; ++i)
    {
        h = SplitMixOut(h + kSplitMixGamma * (i + 1u) + fields[i]);
    }

    return h;
}

// The key's top 24 bits scaled to [0, 1).
inline float KeyedUnit(uint64_t key)
{
    return static_cast<float>(static_cast<uint32_t>(key >> 40)) * (1.0f / 16777216.0f);
}
}  // namespace sacs
}  // namespace simlink
