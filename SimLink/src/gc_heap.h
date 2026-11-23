// The GC heap lever (native version 14; on by default from 15). SimLink rewrites the immediates of Boehm's initial
// heap (GC_init) and of its growth step (GC_collect_or_expand's three clamps) at load, after checking their bytes,
// all four or none: to 352 MB and 32 MB when SIMLINK_GCHEAP is unset, to SIMLINK_GCHEAP=<initialMB>,<growthMB>,
// or not at all with SIMLINK_GCHEAP=0. With
// SIMLINK_GCFORCED=1, on its own, it also times the forced collections EA's GC_collect_or_expand runs instead of
// growing the heap (the thunk at 0xD70800 retargeted to a timer that calls the collection). The counters read the
// live heap size, GC_gc_no and the process's commit whether or not either is on. The rows and why: gc_heap_logic.h.
// See gc_heap.cpp.
#pragma once

#include <windows.h>

namespace simlink
{
namespace gcheap
{
enum CounterId : int
{
    CounterState = 0,         // the heap rows' install state | detail << 8 (sacs::PatchState values and the two below)
    CounterSites = 1,         // heap rows written (4 when installed)
    CounterInitialMB = 2,     // SIMLINK_GCHEAP's initial heap as parsed (352 when unset; 0 when off or refused)
    CounterGrowthMB = 3,      // SIMLINK_GCHEAP's growth step as parsed
    CounterHeapMB = 4,        // GC_heapsize now, MB (-1 when the pins did not match)
    CounterGcNo = 5,          // GC_gc_no now (-1 when the pins did not match)
    CounterTimerState = 6,    // the forced timer's install state | detail << 8
    CounterForced = 7,        // forced collections timed
    CounterForcedMs = 8,      // their total, ms
    CounterForcedMaxUs = 9,   // the longest, microseconds
    CounterForcedLastUs = 10, // the last, microseconds
    CounterForcedOver50 = 11, // those of 50 ms or more
    CounterForcedOver100 = 12,  // those of 100 ms or more
    CounterCommitMB = 13,     // the process's commit (PagefileUsage) now, MB
    CounterPeakCommitMB = 14, // its peak commit (PeakPagefileUsage), MB
    CounterInitAtLoad = 15,   // GC_is_initialized when SimLink loaded: 0 (the heap rows can take), 1, -1 pins differ
    CounterIds = 16,
};

// Install states beyond sacs::PatchState's, for the heap rows.
constexpr int kStateBadSpec = 7;       // SIMLINK_GCHEAP set but malformed (detail 2) or out of bounds (detail 3)
constexpr int kStateInitialized = 8;   // GC_init had already run: the initial row would be dead, nothing written

// Called once from DllMain (process attach, TS3W.exe only), after sacs::Install. Checks the pins (no write), then the
// heap rows unless SIMLINK_GCHEAP=0, and the timer with SIMLINK_GCFORCED=1; the kill switch sacs::Install found
// (SIMLINK_NOPATCH, SimLink.nopatch) covers both. Never throws.
void Install(HMODULE self);

// ---- internal call (cdecl) ----
int __cdecl Counter(int which);
}  // namespace gcheap
}  // namespace simlink
