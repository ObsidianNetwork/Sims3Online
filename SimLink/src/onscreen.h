// the on-screen walk (native version 12). Unless the process is started with SIMLINK_ONSCREEN=0, SimLink
// rewrites the entry of the route follower's on-screen test at load, after checking its bytes, so that every Sim walks
// the animated, on-screen way whatever the game's cameras look at. With it nothing is written. The row and why:
// onscreen_logic.h. See onscreen.cpp.
#pragma once

#include <windows.h>

namespace simlink
{
namespace onscreen
{
enum CounterId : int
{
    CounterState = 0,  // install state | detail << 8 (sacs::PatchState values; detail = row index + 1)
    CounterSites = 1,  // rows written (1 when installed)
    CounterIds = 2,
};

// Called once from DllMain (process attach, TS3W.exe only), after sacs::Install. On by default: SIMLINK_ONSCREEN=0 in
// the process's environment turns it off (state 0); the kill switch sacs::Install found (SIMLINK_NOPATCH,
// SimLink.nopatch) covers it too. Never throws.
void Install(HMODULE self);

// ---- internal call (cdecl) ----
int __cdecl Counter(int which);
}  // namespace onscreen
}  // namespace simlink
