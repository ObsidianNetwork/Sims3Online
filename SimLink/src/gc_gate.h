// the GC rate gate (native version 7). In a process started with SIMLINK_GCGATE=1, SimLink retargets the
// frame loop's one call into the collector at load, after checking its bytes; switch bit 5 (kSwitchGcGate, in
// sacs::SetSwitches' mask) turns the gate on. With the bit off the call passes through unchanged. Without the
// variable nothing is written. See gc_gate.cpp.
#pragma once

#include <windows.h>

#include <cstdint>

namespace simlink
{
namespace gc
{
constexpr int32_t kSwitchGcGate = 32;  // sacs::SetSwitches bit 5: the gate decides the frame's collection call
constexpr int32_t kDefaultIntervalMs = 6000;

enum CounterId : int
{
    CounterCalls = 0,        // every entry, bit on or off
    CounterAdmitted = 1,     // bit on: whole collections run (the never-stop function)
    CounterSkipped = 2,      // bit on: frames with no collection call
    CounterHeldAtEntry = 3,  // bit on, after its first admitted call: entries that found a collection in progress
    CounterMeanUs = 4,       // admitted calls' mean duration, microseconds
    CounterMaxUs = 5,        // admitted calls' longest duration, microseconds
    CounterLastUs = 6,       // the last admitted call's duration, microseconds
    CounterState = 7,        // install state | detail << 8 (sacs::PatchState values; detail = check index + 1)
    CounterCount = 8,
};

// Called once from DllMain (process attach, TS3W.exe only), after sacs::Install. Opt-in: it installs only with
// SIMLINK_GCGATE=1 in the process's environment (else state 0); the kill switch sacs::Install found (SIMLINK_NOPATCH,
// SimLink.nopatch) covers the gate too. Never throws.
void Install(HMODULE self);

// ---- internal calls (cdecl) ----
int __cdecl GateCounter(int which);
int __cdecl GateInterval(int ms);  // ms < 0: query only. Returns the previous interval.
}  // namespace gc
}  // namespace simlink
