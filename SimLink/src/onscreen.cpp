// the on-screen walk. The address and bytes come from a disassembly of the Steam 1.67.2.024037 TS3W.exe
// (image base 0x400000, no ASLR). The row's bytes are checked before anything is written. The row and why:
// onscreen_logic.h.
//
// On by default in every game from native version 12, server and client: the server's authoritative walk and every
// copy's walk are then the same animated walk, whatever each game's camera looks at (two-hud-server-away).
// SIMLINK_ONSCREEN=0 turns it off, for an A/B's base arm. Checked and installed on its own: a byte mismatch writes nothing. The tier 3
// kill switch (SIMLINK_NOPATCH, SimLink.nopatch) covers it too. Written at load, before the game routes a Sim.
#include "onscreen.h"

#include <cstring>

#include "onscreen_logic.h"
#include "sacs_patch.h"

namespace simlink
{
bool RegistrarBytesMatch();  // sidecar.cpp: pins the build

namespace onscreen
{
namespace
{
volatile LONG g_state = sacs::PatchNotAttempted;
volatile LONG g_detail = 0;
volatile LONG g_sites = 0;

void SetState(sacs::PatchState state, LONG detail)
{
    InterlockedExchange(&g_detail, detail);
    InterlockedExchange(&g_state, state);
}

bool Wanted()
{
    wchar_t value[8];
    const DWORD length = GetEnvironmentVariableW(L"SIMLINK_ONSCREEN", value, 8);
    return OnScreenWanted(value, length);
}

// Writes `bytes` over the row's window; false when its page could not be made writable.
bool WriteRow(const ByteReplace& row, const unsigned char* bytes)
{
    void* at = reinterpret_cast<void*>(row.va);
    DWORD old = 0;
    if (!VirtualProtect(at, row.length, PAGE_EXECUTE_READWRITE, &old))
    {
        return false;
    }

    std::memcpy(at, bytes, row.length);
    DWORD unused = 0;
    VirtualProtect(at, row.length, old, &unused);
    FlushInstructionCache(GetCurrentProcess(), at, row.length);
    return true;
}

void InstallChecked()
{
    if (!RegistrarBytesMatch())
    {
        SetState(sacs::PatchBuildMismatch, 0);
        return;
    }

    for (size_t i = 0; i < kOnScreenCount; ++i)
    {
        const ByteReplace& row = kOnScreenReplaces[i];
        if (std::memcmp(reinterpret_cast<const void*>(row.va), row.original, row.length) != 0)
        {
            SetState(sacs::PatchSiteMismatch, static_cast<LONG>(i + 1));
            return;
        }
    }

    for (size_t i = 0; i < kOnScreenCount; ++i)
    {
        if (!WriteRow(kOnScreenReplaces[i], kOnScreenReplaces[i].replacement))
        {
            for (size_t j = i; j-- > 0;)
            {
                WriteRow(kOnScreenReplaces[j], kOnScreenReplaces[j].original);  // all or none
            }

            InterlockedExchange(&g_sites, 0);
            SetState(sacs::PatchProtectFailed, static_cast<LONG>(i + 1));
            return;
        }

        InterlockedIncrement(&g_sites);
    }

    SetState(sacs::PatchInstalled, 0);
}
}  // namespace

void Install(HMODULE)
{
    if (!Wanted())
    {
        return;  // state 0, not attempted (SIMLINK_ONSCREEN=0): the stock camera test
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
        SetState(sacs::PatchException, 0);
    }
}

int __cdecl Counter(int which)
{
    switch (which)
    {
    case CounterState:
        return static_cast<int>(g_state) | (static_cast<int>(g_detail) << 8);
    case CounterSites:
        return static_cast<int>(g_sites);
    default:
        return 0;
    }
}
}  // namespace onscreen
}  // namespace simlink
