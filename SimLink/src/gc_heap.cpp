// The GC heap lever (native version 14). Addresses and site digests correspond to Steam 1.67.2.024037 TS3W.exe
// (image base 0x400000, no ASLR). The rows and why: gc_heap_logic.h.
//
// On by default from native version 15 (352 MB and 32 MB; SIMLINK_GCHEAP=<initialMB>,<growthMB> for other values,
// SIMLINK_GCHEAP=0 for the stock heap): SimLink writes the four heap rows at load, all or none, after the pins
// below and every row's original bytes match, and only while GC_init has not run yet (GC_is_initialized 0: the
// initial row is read once, by GC_init). A malformed or out-of-bounds value writes nothing (state 7), and so does a
// GC that is already up (state 8). SIMLINK_GCFORCED=1, on its own, retargets the forced-collection thunk at 0xD70800
// (jmp 0xE4A320; its one caller is EA's incremental branch of GC_collect_or_expand, 0xE4A1B4) to ForcedCollect, which
// times the collection and counts it; the collection itself is unchanged. The tier 3 kill switch (SIMLINK_NOPATCH,
// SimLink.nopatch) covers both. The counters also read GC_heapsize, GC_gc_no and the process's commit, from any
// game whose pins matched, so an A/B's base arm has the same readings as its heap arm.
#include "gc_heap.h"

#include <psapi.h>

#include <climits>
#include <cstring>

#include "gc_heap_logic.h"
#include "sacs_patch.h"
#include "site_digest.h"

namespace simlink
{
bool RegistrarBytesMatch();  // sidecar.cpp: pins the build

namespace gcheap
{
namespace
{
typedef void(__cdecl* NeverStopCollectFn)();
constexpr uintptr_t kInitializedAt = 0x011F30D0;  // GC_is_initialized (pinned by kHeapInitedUse)
constexpr uintptr_t kHeapSizeAt = 0x012225A0;     // GC_heapsize, bytes (pinned by kHeapSizeUse)
constexpr uintptr_t kGcNoAt = 0x011F2F90;         // GC_gc_no (pinned by kHeapGcNoUse)

volatile LONG g_state = sacs::PatchNotAttempted;
volatile LONG g_detail = 0;
volatile LONG g_sites = 0;
volatile LONG g_timerState = sacs::PatchNotAttempted;
volatile LONG g_timerDetail = 0;
volatile LONG g_pinsOk = 0;
volatile LONG g_initAtLoad = -1;
HeapSpec g_spec = {};
volatile LONG g_counters[CounterIds];
uint64_t g_forcedUs = 0;  // ForcedCollect's alone: Boehm runs it under its allocation lock
LARGE_INTEGER g_frequency = {};

uint64_t NowUs()
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const uint64_t ticks = static_cast<uint64_t>(now.QuadPart);
    const uint64_t hz = static_cast<uint64_t>(g_frequency.QuadPart);
    return ticks / hz * 1000000u + ticks % hz * 1000000u / hz;
}

LONG Clamp(uint64_t value)
{
    return value > static_cast<uint64_t>(LONG_MAX) ? LONG_MAX : static_cast<LONG>(value);
}

// The forced collection, timed. Entered through the thunk's jmp from GC_collect_or_expand's call, so it returns
// straight to that caller; the collection takes no arguments and its caller reads no register after it.
void __cdecl ForcedCollect()
{
    const uint64_t start = NowUs();
    reinterpret_cast<NeverStopCollectFn>(kNeverStopCollectAt)();
    const uint64_t took = NowUs() - start;
    g_forcedUs += took;
    InterlockedIncrement(&g_counters[CounterForced]);
    InterlockedExchange(&g_counters[CounterForcedMs], Clamp(g_forcedUs / 1000u));
    InterlockedExchange(&g_counters[CounterForcedLastUs], Clamp(took));
    if (Clamp(took) > g_counters[CounterForcedMaxUs])
    {
        InterlockedExchange(&g_counters[CounterForcedMaxUs], Clamp(took));
    }

    if (took >= kLongPauseUs)
    {
        InterlockedIncrement(&g_counters[CounterForcedOver50]);
    }

    if (took >= kVeryLongPauseUs)
    {
        InterlockedIncrement(&g_counters[CounterForcedOver100]);
    }
}

// Pinned build verification digests (128-bit SiteDigest) for GC_init's GC_is_initialized test, GC_expand_hp_inner
// call, GC_heapsize read, growth call, GC_gc_no increment, forced thunk call, and never-stop collect entry.
struct Check
{
    uintptr_t va;
    size_t length;
    SiteDigest digest;
};

struct ForcedJump
{
    uintptr_t va;
    size_t length;
    const void* target;
};

const Check kHeapChecks[] = {
    {0x00E4E160, 7, {0xDA309A8643171DF5ULL, 0x0E886DCACAEEE308ULL}},   // GC_is_initialized pin
    {0x00E4E36D, 6, {0xEB64049675176BD0ULL, 0x4904DA1962F684EAULL}},   // GC_init expand call pin
    {0x00E4A1C0, 14, {0xC0FC005C13FF1F6DULL, 0x19217383B367B1E8ULL}},  // GC_heapsize read pin
    {0x00E4A20C, 5, {0xA42BAACCDA0E0546ULL, 0x81D9A92372E058A1ULL}},   // GC_collect_or_expand growth call pin
    {0x00E4A01B, 7, {0xCA7C0888A3AF6170ULL, 0x8E248A551F40CE18ULL}},   // GC_gc_no increment pin
    {0x00E4A1B4, 5, {0x0C14C98C2A07B068ULL, 0x451FF5EAD38F1918ULL}},   // forced collection call pin
    {0x00E4A320, 9, {0x23C1B2E7B21BDCB6ULL, 0x675A3D69EBA5F399ULL}},   // never-stop collect entry pin
};
constexpr size_t kCheckCount = sizeof(kHeapChecks) / sizeof(kHeapChecks[0]);

const ForcedJump kForcedJumps[] = {
    {0x00D70800, sizeof(kHeapForcedThunk), reinterpret_cast<const void*>(&ForcedCollect)},
};

void SetState(LONG state, LONG detail)
{
    InterlockedExchange(&g_detail, detail);
    InterlockedExchange(&g_state, state);
}

void SetTimerState(LONG state, LONG detail)
{
    InterlockedExchange(&g_timerDetail, detail);
    InterlockedExchange(&g_timerState, state);
}

SpecResult ReadSpec(HeapSpec& spec)
{
    wchar_t value[16];
    const DWORD length = GetEnvironmentVariableW(L"SIMLINK_GCHEAP", value, 16);
    return ParseSpec(value, length, 16, spec);
}

bool TimerWanted()
{
    wchar_t value[8];
    const DWORD length = GetEnvironmentVariableW(L"SIMLINK_GCFORCED", value, 8);
    return ForcedTimerWanted(value, length);
}

// 0 when every pin matches, else the first one's index + 1.
LONG FirstBadPin()
{
    for (size_t i = 0; i < kCheckCount; ++i)
    {
        if (!SiteDigestMatches(kHeapChecks[i].va, kHeapChecks[i].length, kHeapChecks[i].digest))
        {
            return static_cast<LONG>(i + 1);
        }
    }

    return 0;
}

// Writes `length` bytes over `va`; false when its page could not be made writable.
bool WriteBytes(uintptr_t va, const unsigned char* bytes, size_t length)
{
    void* at = reinterpret_cast<void*>(va);
    DWORD old = 0;
    if (!VirtualProtect(at, length, PAGE_EXECUTE_READWRITE, &old))
    {
        return false;
    }

    std::memcpy(at, bytes, length);
    DWORD unused = 0;
    VirtualProtect(at, length, old, &unused);
    FlushInstructionCache(GetCurrentProcess(), at, length);
    return true;
}

// The heap rows. Detail: 1-7 a pin (kHeapChecks), 8-11 a row (kHeapReplaces); for state 7 the SpecResult.
void InstallHeap(SpecResult result, const HeapSpec& spec, bool killed, LONG badPin)
{
    if (result == SpecOff)
    {
        return;  // state 0, not attempted: the stock 40 MB heap and 8 MB step
    }

    if (result != SpecOk && result != SpecDefault)
    {
        SetState(kStateBadSpec, result);
        return;
    }

    g_spec = spec;
    if (killed)
    {
        SetState(sacs::PatchKillSwitch, 0);
        return;
    }

    if (badPin != 0)
    {
        SetState(sacs::PatchSiteMismatch, badPin);
        return;
    }

    if (g_initAtLoad != 0)
    {
        SetState(kStateInitialized, 0);
        return;
    }

    unsigned char rows[kHeapCount][kHeapRowMax];
    for (size_t i = 0; i < kHeapCount; ++i)
    {
        const ByteReplace& row = kHeapReplaces[i];
        if (std::memcmp(reinterpret_cast<const void*>(row.va), row.original, row.length) != 0)
        {
            SetState(sacs::PatchSiteMismatch, static_cast<LONG>(kCheckCount + i + 1));
            return;
        }

        BuildRow(row, RowBlocks(i, spec), rows[i]);
    }

    for (size_t i = 0; i < kHeapCount; ++i)
    {
        if (!WriteBytes(kHeapReplaces[i].va, rows[i], kHeapReplaces[i].length))
        {
            for (size_t j = i; j-- > 0;)
            {
                WriteBytes(kHeapReplaces[j].va, kHeapReplaces[j].original, kHeapReplaces[j].length);  // all or none
            }

            InterlockedExchange(&g_sites, 0);
            SetState(sacs::PatchProtectFailed, static_cast<LONG>(kCheckCount + i + 1));
            return;
        }

        InterlockedIncrement(&g_sites);
    }

    SetState(sacs::PatchInstalled, 0);
}

// The forced timer. Detail: 1-7 a pin, 12 the thunk.
void InstallTimer(bool wanted, bool killed, LONG badPin)
{
    if (!wanted)
    {
        return;  // state 0, not attempted: the thunk as it is
    }

    if (killed)
    {
        SetTimerState(sacs::PatchKillSwitch, 0);
        return;
    }

    if (badPin != 0)
    {
        SetTimerState(sacs::PatchSiteMismatch, badPin);
        return;
    }

    const ForcedJump& jump = kForcedJumps[0];
    const LONG thunkDetail = static_cast<LONG>(kCheckCount + kHeapCount + 1);
    if (std::memcmp(reinterpret_cast<const void*>(jump.va), kHeapForcedThunk, jump.length) != 0 ||
        JumpTarget(jump.va, kHeapForcedThunk) != kNeverStopCollectAt)
    {
        SetTimerState(sacs::PatchSiteMismatch, thunkDetail);
        return;
    }

    unsigned char jmp[5] = {0xE9, 0, 0, 0, 0};
    const int32_t rel = static_cast<int32_t>(reinterpret_cast<uintptr_t>(jump.target) - (jump.va + 5));
    std::memcpy(jmp + 1, &rel, 4);
    if (!WriteBytes(jump.va, jmp, sizeof(jmp)))
    {
        SetTimerState(sacs::PatchProtectFailed, thunkDetail);
        return;
    }

    SetTimerState(sacs::PatchInstalled, 0);
}

void InstallChecked(bool clock)
{
    HeapSpec spec = {};
    const SpecResult result = ReadSpec(spec);
    const bool timer = TimerWanted();
    if (timer && !clock)
    {
        SetTimerState(sacs::PatchException, 0);  // no performance counter to time with: the thunk as it is
    }

    if (!RegistrarBytesMatch())
    {
        if (result != SpecOff)
        {
            SetState(sacs::PatchBuildMismatch, 0);
        }

        if (timer)
        {
            SetTimerState(sacs::PatchBuildMismatch, 0);
        }

        return;
    }

    const LONG badPin = FirstBadPin();
    if (badPin == 0)
    {
        InterlockedExchange(&g_initAtLoad, *reinterpret_cast<volatile LONG*>(kInitializedAt) != 0 ? 1 : 0);
        InterlockedExchange(&g_pinsOk, 1);
    }

    const bool killed = (sacs::PatchStateWord() & 0xFF) == sacs::PatchKillSwitch;
    InstallHeap(result, spec, killed, badPin);
    InstallTimer(timer && clock, killed, badPin);
}

int CommitMB(bool peak)
{
    PROCESS_MEMORY_COUNTERS counters = {};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof(counters)))
    {
        return -1;
    }

    return static_cast<int>((peak ? counters.PeakPagefileUsage : counters.PagefileUsage) >> 20);
}
}  // namespace

void Install(HMODULE)
{
    const bool clock = QueryPerformanceFrequency(&g_frequency) && g_frequency.QuadPart > 0;
    __try
    {
        InstallChecked(clock);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        SetState(sacs::PatchException, 0);
        SetTimerState(sacs::PatchException, 0);
    }
}

int __cdecl Counter(int which)
{
    const bool pins = g_pinsOk != 0;
    switch (which)
    {
    case CounterState:
        return static_cast<int>(g_state) | (static_cast<int>(g_detail) << 8);
    case CounterSites:
        return static_cast<int>(g_sites);
    case CounterInitialMB:
        return static_cast<int>(g_spec.initialMB);
    case CounterGrowthMB:
        return static_cast<int>(g_spec.growthMB);
    case CounterHeapMB:
        return pins ? static_cast<int>(*reinterpret_cast<volatile uint32_t*>(kHeapSizeAt) >> 20) : -1;
    case CounterGcNo:
        return pins ? static_cast<int>(*reinterpret_cast<volatile uint32_t*>(kGcNoAt)) : -1;
    case CounterTimerState:
        return static_cast<int>(g_timerState) | (static_cast<int>(g_timerDetail) << 8);
    case CounterCommitMB:
        return CommitMB(false);
    case CounterPeakCommitMB:
        return CommitMB(true);
    case CounterInitAtLoad:
        return static_cast<int>(g_initAtLoad);
    default:
        return which >= CounterForced && which <= CounterForcedOver100 ? static_cast<int>(g_counters[which]) : 0;
    }
}
}  // namespace gcheap
}  // namespace simlink
