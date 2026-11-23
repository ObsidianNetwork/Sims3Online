// the GC rate gate. Addresses and site digests correspond to Steam 1.67.2.024037 TS3W.exe (image base 0x400000,
// no ASLR); every site is digest-checked before anything is written.
//
// RunScriptHostFrameLoop calls the Boehm collector's step 0xE4A050 (GC_try_to_collect's shape: cdecl, one argument,
// the stop function) once a frame at 0xD819AA, with the time-budget stop function 0xD717B0, so a collection's mark
// work spreads over many frames. In a process started with SIMLINK_GCGATE=1 (opt-in, meant for the server game only),
// SimLink retargets that one call to Gate at load; without it nothing is written and the status reads installed=0.
// The call's rel32 (0xD819AB) is not aligned, so it is written before the frame loop first runs, never while the game
// runs it.
//
// With switch bit 5 off, Gate calls 0xE4A050 with the caller's stop function: the game's own behaviour. With it on
// (the mod's gc.gate, server only), Gate calls 0xE4A050 with the never-stop function 0x5BFB90 (xor eax, eax / ret),
// so the collection runs to its end in one frame, when a collection is in progress (GC_mark_state 0x11F3078 not 0) or
// when gc.gate-interval-ms has passed since GC_gc_no (0x11F2F90) last changed; otherwise it makes no call. The
// caller pops the argument and does not use the result. The decision is gc_gate_logic.h; GateCounter reads the
// counters SACS_RECORDER status reports (SacsGc.cs).
//
// Checked and installed on its own: a byte mismatch leaves the call as it is and tier 3 untouched. The tier 3 kill
// switch (SIMLINK_NOPATCH, SimLink.nopatch) covers it too.
#include "gc_gate.h"

#include <climits>
#include <cstring>

#include "gc_gate_logic.h"
#include "sacs_patch.h"
#include "site_digest.h"

namespace simlink
{
bool RegistrarBytesMatch();  // sidecar.cpp: pins the build

namespace gc
{
namespace
{
typedef int(__cdecl* CollectFn)(void* stop);
const CollectFn kCollect = reinterpret_cast<CollectFn>(0x00E4A050);
void* const kNeverStop = reinterpret_cast<void*>(0x005BFB90);
constexpr uintptr_t kGcNoAt = 0x011F2F90;      // GC_gc_no (pinned by kGcNoUse)
constexpr uintptr_t kMarkStateAt = 0x011F3078;  // GC_mark_state (pinned by kMarkStateUse)

volatile LONG g_state = sacs::PatchNotAttempted;
volatile LONG g_detail = 0;
volatile LONG g_intervalMs = kDefaultIntervalMs;
volatile LONG g_counters[CounterCount];
GateMemory g_memory = {};      // the main thread's alone: only the frame loop calls Gate
uint64_t g_admittedUs = 0;
LARGE_INTEGER g_frequency = {};

uint64_t NowUs()
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const uint64_t ticks = static_cast<uint64_t>(now.QuadPart);
    const uint64_t hz = static_cast<uint64_t>(g_frequency.QuadPart);
    return ticks / hz * 1000000u + ticks % hz * 1000000u / hz;
}

LONG Clamp(uint64_t us)
{
    return us > static_cast<uint64_t>(LONG_MAX) ? LONG_MAX : static_cast<LONG>(us);
}

int __cdecl Gate(void* stop)
{
    InterlockedIncrement(&g_counters[CounterCalls]);
    GateInput in;
    in.bitOn = (sacs::SetSwitches(-1) & kSwitchGcGate) != 0;
    in.gcNo = *reinterpret_cast<volatile uint32_t*>(kGcNoAt);
    in.markState = *reinterpret_cast<volatile uint32_t*>(kMarkStateAt);
    in.nowUs = NowUs();
    in.intervalMs = static_cast<uint32_t>(g_intervalMs);
    bool held = false;
    const Decision decision = Decide(g_memory, in, held);
    if (held)
    {
        InterlockedIncrement(&g_counters[CounterHeldAtEntry]);
    }

    if (decision == DecisionPass)
    {
        return kCollect(stop);
    }

    if (decision == DecisionSkip)
    {
        InterlockedIncrement(&g_counters[CounterSkipped]);
        return 0;
    }

    const uint64_t start = NowUs();
    const int result = kCollect(kNeverStop);
    const uint64_t end = NowUs();
    Admitted(g_memory, *reinterpret_cast<volatile uint32_t*>(kGcNoAt), end);
    const LONG admitted = InterlockedIncrement(&g_counters[CounterAdmitted]);
    const uint64_t took = end - start;
    g_admittedUs += took;
    InterlockedExchange(&g_counters[CounterMeanUs], Clamp(g_admittedUs / static_cast<uint64_t>(admitted)));
    InterlockedExchange(&g_counters[CounterLastUs], Clamp(took));
    if (Clamp(took) > g_counters[CounterMaxUs])
    {
        InterlockedExchange(&g_counters[CounterMaxUs], Clamp(took));
    }

    return result;
}

// Pinned build verification digests (128-bit SiteDigest) for the frame loop's collector call, the collector's entry,
// the never-stop stop function, GC_gc_no, and GC_mark_state.
struct Check
{
    uintptr_t va;
    size_t length;
    SiteDigest digest;
};

struct Retarget
{
    uintptr_t call;  // the E8 byte
    const void* target;
};

const Check kGcChecks[] = {
    {0x00D819A0, 23, {0xD46BD0D76753C96CULL, 0x0D35A770585E6827ULL}},  // frame loop collection call window
    {0x00E4A050, 15, {0xCB62A856239795AFULL, 0xA79F6842CEFE36B2ULL}},  // Boehm GC_try_to_collect entry
    {0x005BFB90, 3, {0xAAFF3A79EE228E27ULL, 0xD003BE26B8D5596CULL}},   // never-stop function
    {0x00E4A01B, 7, {0xCA7C0888A3AF6170ULL, 0x8E248A551F40CE18ULL}},   // GC_gc_no increment pin
    {0x00E4AFEC, 7, {0x2D827E25830F663AULL, 0x465B09E8DD50FCC0ULL}},   // GC_mark_state pin
};

const Retarget kGcRetargets[] = {
    {0x00D819AA, reinterpret_cast<const void*>(&Gate)},
};

void SetState(sacs::PatchState state, LONG detail)
{
    InterlockedExchange(&g_detail, detail);
    InterlockedExchange(&g_state, state);
}

// The gate is opt-in: only a process started with SIMLINK_GCGATE=1 (the server controller's --server-env) gets it.
bool OptedIn()
{
    wchar_t value[8];
    const DWORD length = GetEnvironmentVariableW(L"SIMLINK_GCGATE", value, 8);
    return length == 1 && value[0] == L'1';
}

void InstallChecked()
{
    if (!RegistrarBytesMatch())
    {
        SetState(sacs::PatchBuildMismatch, 0);
        return;
    }

    for (size_t i = 0; i < sizeof(kGcChecks) / sizeof(kGcChecks[0]); ++i)
    {
        if (!SiteDigestMatches(kGcChecks[i].va, kGcChecks[i].length, kGcChecks[i].digest))
        {
            SetState(sacs::PatchSiteMismatch, static_cast<LONG>(i + 1));
            return;
        }
    }

    for (size_t i = 0; i < sizeof(kGcRetargets) / sizeof(kGcRetargets[0]); ++i)
    {
        unsigned char* call = reinterpret_cast<unsigned char*>(kGcRetargets[i].call);
        DWORD old = 0;
        if (call[0] != 0xE8 || !VirtualProtect(call, 5, PAGE_EXECUTE_READWRITE, &old))
        {
            SetState(sacs::PatchProtectFailed, static_cast<LONG>(i + 1));
            return;
        }

        const int32_t rel = static_cast<int32_t>(reinterpret_cast<uintptr_t>(kGcRetargets[i].target) -
                                                 (kGcRetargets[i].call + 5));
        std::memcpy(call + 1, &rel, 4);
        DWORD unused = 0;
        VirtualProtect(call, 5, old, &unused);
        FlushInstructionCache(GetCurrentProcess(), call, 5);
    }

    SetState(sacs::PatchInstalled, 0);
}
}  // namespace

void Install(HMODULE)
{
    if (!OptedIn())
    {
        return;  // state 0, not attempted: the stock call path
    }

    if ((sacs::PatchStateWord() & 0xFF) == sacs::PatchKillSwitch)
    {
        SetState(sacs::PatchKillSwitch, 0);
        return;
    }

    if (!QueryPerformanceFrequency(&g_frequency) || g_frequency.QuadPart <= 0)
    {
        SetState(sacs::PatchException, 0);
        return;
    }

    __try
    {
        InstallChecked();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        SetState(sacs::PatchException, 0);
    }
}

int __cdecl GateCounter(int which)
{
    if (which == CounterState)
    {
        return static_cast<int>(g_state) | (static_cast<int>(g_detail) << 8);
    }

    return which >= 0 && which < CounterCount ? static_cast<int>(g_counters[which]) : 0;
}

int __cdecl GateInterval(int ms)
{
    return ms < 0 ? static_cast<int>(g_intervalMs) : static_cast<int>(InterlockedExchange(&g_intervalMs, ms));
}
}  // namespace gc
}  // namespace simlink
