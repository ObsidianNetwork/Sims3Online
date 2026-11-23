// the chunk reserve. Addresses and bytes come from a disassembly of the Steam 1.67.2.024037 TS3W.exe (image
// base 0x400000, no ASLR). Every row's bytes are checked before anything is written, and a failed write puts back the
// rows already written, so the rows go in all together or not at all. The rows and why: chunk_reserve_logic.h.
//
// On by default in every game from native version 11, server and client: series R and K (10-20) saw no zeroed
// descriptor with it, against 20, 21 (server) and 7 (client) without. SIMLINK_CHUNKRESERVE=0 turns it off, for an
// A/B's base arm; version 10 wrote it only with SIMLINK_CHUNKRESERVE=1. Checked and installed on its own: a byte
// mismatch writes nothing. The tier 3 kill switch (SIMLINK_NOPATCH, SimLink.nopatch) covers it too. Written at load,
// before the script host runs a frame.
#include "chunk_reserve.h"

#include <cstring>

#include "chunk_reserve_logic.h"
#include "sacs_patch.h"

namespace simlink
{
bool RegistrarBytesMatch();  // sidecar.cpp: pins the build

namespace chunkreserve
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
    const DWORD length = GetEnvironmentVariableW(L"SIMLINK_CHUNKRESERVE", value, 8);
    return ReserveWanted(value, length);
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

    for (size_t i = 0; i < kReserveCount; ++i)
    {
        const ByteReplace& row = kReserveReplaces[i];
        if (std::memcmp(reinterpret_cast<const void*>(row.va), row.original, row.length) != 0)
        {
            SetState(sacs::PatchSiteMismatch, static_cast<LONG>(i + 1));
            return;
        }
    }

    for (size_t i = 0; i < kReserveCount; ++i)
    {
        if (!WriteRow(kReserveReplaces[i], kReserveReplaces[i].replacement))
        {
            for (size_t j = i; j-- > 0;)
            {
                WriteRow(kReserveReplaces[j], kReserveReplaces[j].original);  // all or none
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
        return;  // state 0, not attempted (SIMLINK_CHUNKRESERVE=0): the stock fit tests
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
}  // namespace chunkreserve
}  // namespace simlink
