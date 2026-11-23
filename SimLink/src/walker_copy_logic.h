// option C (walker counter carry): a client that meets a server
// driver mid-life copies the server's walker counters instead of both games starting them again. The keys of tier 3,
// site A' and item 3 mix the driver's spare seed with per-walker counters SimLink keeps (WalkerEntry); a copy that met
// the driver late, or re-acquired it after kProxyInvalid, would otherwise count from 0 while the server's run on.
// sacs_patch.cpp holds the tables and the exports; selftest.cpp (`simlink_selftest walkers`) checks this file's rules.
// Nothing here reads game memory.
#pragma once

#include <cstdint>

namespace simlink
{
namespace sacs
{
// One walker's counters, keyed by its pointer. Entries are never removed; a spare change (a reused pointer, or a new
// seed) starts them again. driver and index say whose walker it was when its counters (re)started; copiedBoundary is
// boundaries + 1 while the counters copied into it (a write, a pending set taken, a carry) still stand at the boundary
// they came with, 0 otherwise (item 3's per-node evaluations and the game's no-repeat history are not copied, so
// keyed Random evaluates in that window are the residual the live check measures).
struct WalkerEntry
{
    uintptr_t walker;
    uint32_t spare;
    uint32_t generation;  // tier 3: requests
    uint32_t draws;       // tier 3: draws since the last request
    uint32_t executions;  // site A': phase-0 executions logged
    uint32_t boundaries;  // item 3: requests and EnterStates
    uintptr_t driver;
    uint32_t index;
    uint32_t copiedBoundary;
};

struct WalkerCounts
{
    uint32_t generation;
    uint32_t draws;
    uint32_t boundaries;
    uint32_t executions;
};

inline WalkerCounts CountsOf(const WalkerEntry& entry)
{
    WalkerCounts counts = {entry.generation, entry.draws, entry.boundaries, entry.executions};
    return counts;
}

// Puts copied counters on an entry and opens its residual window.
inline void SetCounts(WalkerEntry& entry, const WalkerCounts& counts)
{
    entry.generation = counts.generation;
    entry.draws = counts.draws;
    entry.boundaries = counts.boundaries;
    entry.executions = counts.executions;
    entry.copiedBoundary = counts.boundaries + 1u;
}

// True while the entry's counters still stand at the boundary a copy put there.
inline bool InCopiedWindow(const WalkerEntry& entry)
{
    return entry.copiedBoundary != 0u && entry.boundaries + 1u == entry.copiedBoundary;
}

// After boundaries moved: closes a residual window that was open. True when it closed one.
inline bool CloseWindow(WalkerEntry& entry)
{
    if (entry.copiedBoundary == 0u || entry.boundaries + 1u == entry.copiedBoundary)
    {
        return false;
    }

    entry.copiedBoundary = 0u;
    return true;
}

// Where a copied count set goes (WalkerWrite, WalkerCarry): onto the walker now, or kept until SimLink first meets
// that walker under the spare the counts were read with (the walker does not exist yet, or the copy's driver does not
// carry that spare yet). Counts read under another spare would be zeroed by FindWalker at the walker's next call.
enum Placement : int
{
    PlaceWritten = 1,
    PlacePending = 2,       // the walker does not exist yet
    PlaceSpareDiffers = 3,  // the driver's spare is not the counts' spare (yet)
};

inline int Place(bool walkerExists, uint32_t driverSpare, uint32_t spare)
{
    if (driverSpare != spare)
    {
        return PlaceSpareDiffers;
    }

    return walkerExists ? PlaceWritten : PlacePending;
}

// Count sets waiting for their walker, keyed by (driver, index); taken only under the spare they were read with.
struct PendingWalker
{
    uintptr_t driver;  // 0: free
    uint32_t index;
    uint32_t spare;
    WalkerCounts counts;
    uint32_t stamp;
};

constexpr uint32_t kPendingSlots = 256;

struct PendingTable
{
    PendingWalker slots[kPendingSlots];
    uint32_t clock;
    uint32_t live;
    uint32_t staged;
    uint32_t replaced;  // a newer set for the same walker
    uint32_t taken;
    uint32_t evicted;   // the oldest set dropped for a new one (the table was full)
};

// A newer set for the same (driver, index) replaces the older one whatever its spare; otherwise a free slot, otherwise
// the oldest set goes.
inline void Stage(PendingTable& table, uintptr_t driver, uint32_t index, uint32_t spare, const WalkerCounts& counts)
{
    PendingWalker* free = nullptr;
    PendingWalker* oldest = &table.slots[0];
    PendingWalker* slot = nullptr;
    for (uint32_t i = 0; i < kPendingSlots; ++i)
    {
        PendingWalker& candidate = table.slots[i];
        if (candidate.driver == driver && candidate.index == index)
        {
            slot = &candidate;
            break;
        }

        if (candidate.driver == 0u)
        {
            if (free == nullptr)
            {
                free = &candidate;
            }
        }
        else if (oldest->driver == 0u || candidate.stamp < oldest->stamp)
        {
            oldest = &candidate;
        }
    }

    if (slot != nullptr)
    {
        ++table.replaced;
    }
    else if (free != nullptr)
    {
        slot = free;
        ++table.live;
    }
    else
    {
        slot = oldest;
        ++table.evicted;
    }

    slot->driver = driver;
    slot->index = index;
    slot->spare = spare;
    slot->counts = counts;
    slot->stamp = ++table.clock;
    ++table.staged;
}

// The set for (driver, index) under spare, freed once taken. A set under another spare stays (that spare may come yet).
inline bool Take(PendingTable& table, uintptr_t driver, uint32_t index, uint32_t spare, WalkerCounts& counts)
{
    if (table.live == 0u)
    {
        return false;
    }

    for (uint32_t i = 0; i < kPendingSlots; ++i)
    {
        PendingWalker& slot = table.slots[i];
        if (slot.driver == driver && slot.index == index)
        {
            if (slot.spare != spare)
            {
                return false;
            }

            counts = slot.counts;
            slot.driver = 0u;
            --table.live;
            ++table.taken;
            return true;
        }
    }

    return false;
}

// (Re)starts an entry's counters for walker of driver at index under spare (FindWalker: first use, or a new spare): from
// 0, or from the set kept for that walker under that spare. True when a kept set was taken (its window opens).
inline bool StartCounts(WalkerEntry& entry, uintptr_t walker, uint32_t spare, uintptr_t driver, uint32_t index,
                        PendingTable& pending)
{
    entry.walker = walker;
    entry.spare = spare;
    entry.generation = 0;
    entry.draws = 0;
    entry.executions = 0;
    entry.boundaries = 0;
    entry.driver = driver;
    entry.index = index;
    entry.copiedBoundary = 0;
    WalkerCounts counts;
    if (!Take(pending, driver, index, spare, counts))
    {
        return false;
    }

    SetCounts(entry, counts);
    return true;
}

// A direct write supersedes any set still waiting for that walker.
inline void Forget(PendingTable& table, uintptr_t driver, uint32_t index)
{
    if (table.live == 0u)
    {
        return;
    }

    for (uint32_t i = 0; i < kPendingSlots; ++i)
    {
        PendingWalker& slot = table.slots[i];
        if (slot.driver == driver && slot.index == index)
        {
            slot.driver = 0u;
            --table.live;
            return;
        }
    }
}

// The re-resolve carry: the counters of oldDriver's walkers under spare, one per walker index (when an index has two
// entries, a walker the driver replaced, the one further on: more boundaries, then more requests). Fills found, counts
// and source (the entry's walker pointer) for indexes below maxIndex; returns how many indexes it found.
inline uint32_t CarryCollect(const WalkerEntry* entries, uint32_t n, uintptr_t oldDriver, uint32_t spare,
                             bool* found, WalkerCounts* counts, uintptr_t* source, uint32_t maxIndex)
{
    for (uint32_t i = 0; i < maxIndex; ++i)
    {
        found[i] = false;
    }

    uint32_t indexes = 0;
    for (uint32_t e = 0; e < n; ++e)
    {
        const WalkerEntry& entry = entries[e];
        if (entry.walker == 0u || entry.driver != oldDriver || entry.spare != spare || entry.index >= maxIndex)
        {
            continue;
        }

        const uint32_t i = entry.index;
        if (found[i] && (entry.boundaries < counts[i].boundaries ||
                         (entry.boundaries == counts[i].boundaries && entry.generation <= counts[i].generation)))
        {
            continue;
        }

        if (!found[i])
        {
            ++indexes;
        }

        found[i] = true;
        counts[i] = CountsOf(entry);
        source[i] = entry.walker;
    }

    return indexes;
}

// Handle -> driver as last resolved (the seed and walker calls note every resolve): the carry's old handle no longer
// resolves once the game dropped its driver. Open addressing over a small window; a full window overwrites its first
// slot. Handle 0 is never a driver.
struct HandleEntry
{
    uint32_t handle;
    uintptr_t driver;
};

constexpr uint32_t kHandleSlots = 4096;  // a power of two
constexpr uint32_t kHandleProbe = 32;

inline uint32_t HandleHome(uint32_t handle)
{
    return (handle * 2654435761u) & (kHandleSlots - 1u);
}

inline void NoteHandle(HandleEntry* table, uint32_t handle, uintptr_t driver)
{
    if (handle == 0u)
    {
        return;
    }

    const uint32_t home = HandleHome(handle);
    for (uint32_t probe = 0; probe < kHandleProbe; ++probe)
    {
        HandleEntry& entry = table[(home + probe) & (kHandleSlots - 1u)];
        if (entry.handle == handle || entry.handle == 0u)
        {
            entry.handle = handle;
            entry.driver = driver;
            return;
        }
    }

    table[home].handle = handle;
    table[home].driver = driver;
}

inline uintptr_t DriverOfHandle(const HandleEntry* table, uint32_t handle)
{
    if (handle == 0u)
    {
        return 0u;
    }

    const uint32_t home = HandleHome(handle);
    for (uint32_t probe = 0; probe < kHandleProbe; ++probe)
    {
        const HandleEntry& entry = table[(home + probe) & (kHandleSlots - 1u)];
        if (entry.handle == handle)
        {
            return entry.driver;
        }

        if (entry.handle == 0u)
        {
            return 0u;
        }
    }

    return 0u;
}
}  // namespace sacs
}  // namespace simlink
