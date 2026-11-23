// the chunk reserve (native version 10; on by default from 11). Unless the process is started with
// SIMLINK_CHUNKRESERVE=0, SimLink rewrites three instruction windows at load, after checking their bytes, so that no
// interpreter frame ends at a stack chunk's end, where the chunk's typed GC descriptor is. With it nothing is written.
// The rows and why: chunk_reserve_logic.h. See chunk_reserve.cpp.
#pragma once

#include <windows.h>

namespace simlink
{
namespace chunkreserve
{
enum CounterId : int
{
    CounterState = 0,  // install state | detail << 8 (sacs::PatchState values; detail = row index + 1)
    CounterSites = 1,  // rows written (3 when installed)
    CounterIds = 2,
};

// Called once from DllMain (process attach, TS3W.exe only), after sacs::Install. On by default: SIMLINK_CHUNKRESERVE=0
// in the process's environment turns it off (state 0); the kill switch sacs::Install found (SIMLINK_NOPATCH,
// SimLink.nopatch) covers it too. Never throws.
void Install(HMODULE self);

// ---- internal call (cdecl) ----
int __cdecl Counter(int which);
}  // namespace chunkreserve
}  // namespace simlink
