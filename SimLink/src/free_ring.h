// the explicit-free ring (native version 8; layout 2, with each free's thread, from native version 9). In a
// process started with SIMLINK_FREERING=1, SimLink hooks GC_free's entry (0xE4BEB0) at load, after checking its bytes,
// and records every explicit free (block, size, caller, frame, thread, ...) into SimLink_FreeRing, the last 131072.
// Without the variable nothing is written. Layout:
// free_ring_logic.h. See free_ring.cpp.
#pragma once

#include <windows.h>

namespace simlink
{
namespace freering
{
enum CounterId : int
{
    CounterCount = 0,     // frees recorded (the ring's head)
    CounterPerSec = 1,    // frees per second over the last window of at least a second
    CounterAddress = 2,   // the ring's address (SimLink_FreeRing)
    CounterState = 3,     // install state | detail << 8 (sacs::PatchState values; detail = check index + 1)
    CounterCapacity = 4,  // entries the ring holds
    CounterOffScript = 5,      // frees on another thread than the script thread, once known (native version 8:
                               // frees off the installing thread, which is not the script thread)
    CounterScriptThread = 6,   // the script thread (header +36), 0 until the first frame-release free (native 9)
    CounterInstallThread = 7,  // the thread that installed the hook (native 9)
    CounterLayout = 8,         // the ring's layout version (native 9)
    CounterIds = 9,
};

// Called once from DllMain (process attach, TS3W.exe only), after sacs::Install. Opt-in: it installs only with
// SIMLINK_FREERING=1 in the process's environment (else state 0); the kill switch sacs::Install found (SIMLINK_NOPATCH,
// SimLink.nopatch) covers it too. Never throws.
void Install(HMODULE self);

// ---- internal call (cdecl) ----
int __cdecl Counter(int which);
}  // namespace freering
}  // namespace simlink
