// the explicit-free ring. Addresses and site digests correspond to Steam 1.67.2.024037 TS3W.exe (image base 0x400000,
// no ASLR); every site is digest-checked before anything is written.
//
// The lead: a block freed with GC_free (0xE4BEB0) while something still points to it. The free sets the block's free
// bit, so the marker ignores the stale pointer, and the next allocation of that size reuses the block. The ring keeps
// the last 131072 explicit frees, so a crash report's stale `this` (or the block its word 0 came from) can be looked up.
//
// GC_free is cdecl with one argument. Its entry, `push esi / mov esi, [esp+8]`, is exactly 5 bytes with no relative
// operand, and 16-byte aligned: SimLink writes `jmp rel32` to FreeEntryThunk over them with one 8-byte compare-exchange,
// at load, before the game runs. The thunk records the free (Note), runs the two moved instructions and jumps to
// 0xE4BEB5. The thunk 0xD71810 is a bare jmp to GC_free, so its callers come through the entry too, with [esp] their
// own return address. Every caller and where its frame is: free_ring_logic.h, kFreeSites.
//
// The size and kind come from the block's header, the way GC_free reads it: hdr = top[p >> 22][(p >> 12) & 0x3ff], with
// the top index at 0x1233318; size in words at hdr+0, kind at hdr+0x14. Every read of game memory is inside __try.
//
// Opt-in, meant for the server game only: SIMLINK_FREERING=1 (the server controller's --server-env). Checked and
// installed on its own: a byte mismatch writes nothing. The tier 3 kill switch (SIMLINK_NOPATCH, SimLink.nopatch)
// covers it too.
#include "free_ring.h"

#include <cstring>

#include "free_ring_logic.h"
#include "sacs_patch.h"
#include "site_digest.h"

// The ring, exported so a reader finds it from the .asi's export table (SimLink.asi base + RVA). Zero (.bss) until
// Install fills its header.
extern "C"
{
__declspec(dllexport) simlink::freering::Ring SimLink_FreeRing;
}

namespace simlink
{
bool RegistrarBytesMatch();  // sidecar.cpp: pins the build

namespace freering
{
namespace
{
constexpr uintptr_t kHdrTopAt = 0x01233318;  // the header table's top index (pinned by kHdrTopUse)
constexpr uintptr_t kGcNoAt = 0x011F2F90;    // GC_gc_no (pinned by kGcNoUse)

volatile LONG g_state = sacs::PatchNotAttempted;
volatile LONG g_detail = 0;
volatile LONG g_offScript = 0;
Rate g_rate = {};
LastFrame g_last = {};

uint32_t HeaderOf(uint32_t block)
{
    const uint32_t top = *reinterpret_cast<const uint32_t*>(kHdrTopAt + (block >> 22) * 4);
    if (top < 0x10000)
    {
        return 0;
    }

    const uint32_t hdr = *reinterpret_cast<const uint32_t*>(top + ((block >> 12) & 0x3FF) * 4);
    return hdr < 0x10000 ? 0 : hdr;  // a small value is a large block's forwarding count, not a header
}

// Records one free. `stack` is GC_free's entry ESP ([0] the return address, [1] the block), `ebp` the caller's EBP.
void __cdecl Note(const uint32_t* stack, uint32_t ebp)
{
    Header& header = SimLink_FreeRing.header;
    const uint32_t block = stack[1];
    if (block == 0 || header.enabled == 0)
    {
        return;  // GC_free(0) returns at once
    }

    Entry e = {};
    e.block = block;
    e.caller = stack[0];
    e.caller2 = stack[2];
    e.tickMs = GetTickCount();
    e.tid = GetCurrentThreadId();
    const FreeSite* site = SiteFor(e.caller);
    e.field = site != nullptr ? site->field : static_cast<uint8_t>(FieldOther);
    const uint8_t offScript = ThreadFlag(header.scriptThread, e.field, e.tid);  // claims the script thread once
    if (offScript != 0)
    {
        e.field |= offScript;
        InterlockedIncrement(&g_offScript);
    }

    __try
    {
        e.gcNo = *reinterpret_cast<volatile const uint32_t*>(kGcNoAt);
        e.word0 = *reinterpret_cast<const uint32_t*>(block);
        const uint32_t hdr = HeaderOf(block);
        if (hdr != 0)
        {
            e.sizeWords = *reinterpret_cast<const uint32_t*>(hdr);
            e.kind = *reinterpret_cast<const uint8_t*>(hdr + 0x14);
        }

        e.frame = FrameFor(site, ebp, block, stack);
        if (e.frame != 0 && site->source == FrameBlock && e.frame == g_last.frame)
        {
            e.flags148 = g_last.flags148;  // zeroed by the memset before this free
            e.flags14b = g_last.flags14b;
        }
        else if (e.frame != 0)
        {
            e.flags148 = *reinterpret_cast<const uint8_t*>(e.frame + 0x148);
            e.flags14b = *reinterpret_cast<const uint8_t*>(e.frame + 0x14B);
            if (site->source != FrameBlock)
            {
                g_last.frame = e.frame;
                g_last.flags148 = e.flags148;
                g_last.flags14b = e.flags14b;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
    }

    const uint32_t seq = static_cast<uint32_t>(InterlockedIncrement(&header.head));
    Write(SimLink_FreeRing, seq, e);
    Tick(g_rate, seq, e.tickMs);
}

// Entered by the jmp at GC_free's entry: [esp] is the caller's return address, [esp+4] the block. Note is cdecl and
// keeps EBX, ESI, EDI and EBP; GC_free reads only its argument and writes EAX, ECX and EDX before reading them.
__declspec(naked) void FreeEntryThunk()
{
    __asm {
        mov eax, esp
        push ebp
        push eax
        call Note
        add esp, 8
        push esi                     // GC_free's first two instructions, moved here
        mov esi, dword ptr [esp + 8]
        mov eax, 00E4BEB5h
        jmp eax
    }
}

// Pinned build verification digests (128-bit SiteDigest) for GC_free's entry, thunk, header table pins, GC_gc_no,
// and frame release entry.
constexpr size_t kGcFreeHeadLength = 5;

struct Check
{
    uintptr_t va;
    size_t length;
    SiteDigest digest;
};

struct FreeJump
{
    uintptr_t va;
    size_t length;
    const void* target;
};

const Check kFreeChecks[] = {
    {0x00E4BEB0, 8, {0x4D9EEDF96A6F5488ULL, 0xDB0166BED8970772ULL}},  // Boehm GC_free entry
    {0x00D71810, 5, {0xD51021568BB82927ULL, 0x1F7FF8CCFF02FF05ULL}},  // GC_free jmp thunk
    {0x00E4BECB, 7, {0x5E6C152314AB3DC2ULL, 0x33CD16035618D153ULL}},  // Boehm header table index pin
    {0x00E4BEE1, 8, {0x70A813A8970B4381ULL, 0x43692B1EE864C896ULL}},  // Boehm header fields pin
    {0x00E4A01B, 7, {0xCA7C0888A3AF6170ULL, 0x8E248A551F40CE18ULL}},  // GC_gc_no increment pin
    {0x00E52DD0, 6, {0x66DD2AED99DE134DULL, 0xCB6C61FB97B6F7F4ULL}},  // frame release entry
};

const FreeJump kFreeJumps[] = {
    {0x00E4BEB0, kGcFreeHeadLength, reinterpret_cast<const void*>(&FreeEntryThunk)},
};

void SetState(sacs::PatchState state, LONG detail)
{
    InterlockedExchange(&g_detail, detail);
    InterlockedExchange(&g_state, state);
}

bool OptedIn()
{
    wchar_t value[8];
    const DWORD length = GetEnvironmentVariableW(L"SIMLINK_FREERING", value, 8);
    return length == 1 && value[0] == L'1';
}

void InstallChecked()
{
    if (!RegistrarBytesMatch())
    {
        SetState(sacs::PatchBuildMismatch, 0);
        return;
    }

    for (size_t i = 0; i < sizeof(kFreeChecks) / sizeof(kFreeChecks[0]); ++i)
    {
        if (!SiteDigestMatches(kFreeChecks[i].va, kFreeChecks[i].length, kFreeChecks[i].digest))
        {
            SetState(sacs::PatchSiteMismatch, static_cast<LONG>(i + 1));
            return;
        }
    }

    Header& header = SimLink_FreeRing.header;
    std::memcpy(header.magic, kMagic, sizeof(kMagic));
    header.version = kLayoutVersion;
    header.entrySize = sizeof(Entry);
    header.capacity = kCapacity;
    header.entriesOffset = static_cast<uint32_t>(offsetof(Ring, entries));
    header.installThread = GetCurrentThreadId();
    g_rate.windowTick = GetTickCount();

    const FreeJump& jump = kFreeJumps[0];
    unsigned char* entry = reinterpret_cast<unsigned char*>(jump.va);
    DWORD old = 0;
    if (!VirtualProtect(entry, 8, PAGE_EXECUTE_READWRITE, &old))
    {
        SetState(sacs::PatchProtectFailed, 1);
        return;
    }

    LONG64 was = 0;
    std::memcpy(&was, entry, 8);
    unsigned char now[8];
    std::memcpy(now, &was, 8);
    const int32_t rel = static_cast<int32_t>(reinterpret_cast<uintptr_t>(jump.target) - (jump.va + 5));
    now[0] = 0xE9;
    std::memcpy(now + 1, &rel, 4);
    LONG64 want = 0;
    std::memcpy(&want, now, 8);
    header.enabled = 1;  // before the jmp goes live, so the first free is recorded
    const LONG64 seen = InterlockedCompareExchange64(reinterpret_cast<volatile LONG64*>(entry), want, was);
    DWORD unused = 0;
    VirtualProtect(entry, 8, old, &unused);
    FlushInstructionCache(GetCurrentProcess(), entry, 8);
    if (seen != was)
    {
        header.enabled = 0;
        SetState(sacs::PatchProtectFailed, 2);
        return;
    }

    SetState(sacs::PatchInstalled, 0);
}
}  // namespace

void Install(HMODULE)
{
    if (!OptedIn())
    {
        return;  // state 0, not attempted: GC_free as it is
    }

    if ((sacs::PatchStateWord() & 0xFF) == sacs::PatchKillSwitch)
    {
        SetState(sacs::PatchKillSwitch, 0);
        return;
    }

    __try
    {
        InstallChecked();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        SimLink_FreeRing.header.enabled = 0;
        SetState(sacs::PatchException, 0);
    }
}

int __cdecl Counter(int which)
{
    switch (which)
    {
    case CounterCount:
        return static_cast<int>(SimLink_FreeRing.header.head);
    case CounterPerSec:
        return static_cast<int>(g_rate.perSec);
    case CounterAddress:
        return static_cast<int>(reinterpret_cast<uintptr_t>(&SimLink_FreeRing));
    case CounterState:
        return static_cast<int>(g_state) | (static_cast<int>(g_detail) << 8);
    case CounterCapacity:
        return static_cast<int>(kCapacity);
    case CounterOffScript:
        return static_cast<int>(g_offScript);
    case CounterScriptThread:
        return static_cast<int>(SimLink_FreeRing.header.scriptThread);
    case CounterInstallThread:
        return static_cast<int>(SimLink_FreeRing.header.installThread);
    case CounterLayout:
        return static_cast<int>(kLayoutVersion);
    default:
        return 0;
    }
}
}  // namespace freering
}  // namespace simlink
