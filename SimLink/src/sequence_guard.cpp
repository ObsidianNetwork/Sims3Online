// the sequence notification drain can lose its expected stop node
// during callbacks. Never interpret the exhausted list sentinel as clip data.
// Steam 1.67.2.024037, image base 0x400000: loop RVA 0x167103,
// normal empty-list path RVA 0x1671bf, reported fault RVA 0x167121.
#include "sequence_guard.h"

#include <cstring>

#include "sacs_patch.h"
#include "sequence_guard_logic.h"
#include "site_digest.h"

namespace simlink
{
bool RegistrarBytesMatch();

namespace sequenceguard
{
namespace
{
volatile LONG g_state = sacs::PatchNotAttempted;
volatile LONG g_checks = 0;
volatile LONG g_exhausted = 0;
uintptr_t g_resume = 0x00567108;
uintptr_t g_empty = 0x005671bf;
constexpr uintptr_t kLoop = 0x00567103;
constexpr size_t kLoopLength = 5;
constexpr SiteDigest kLoopDigest = {0x834CDE960D8F9F7FULL, 0x02A5405AAB8061BDULL};  // loop-head entry window (5 bytes)
constexpr size_t kEmptyLength = 8;
constexpr SiteDigest kEmptyDigest = {0x8E1707CF0DCDC39AULL, 0x661182728660638CULL};  // empty-list continuation site (8 bytes)

int __cdecl CheckHead(uintptr_t head, uintptr_t sentinel, uintptr_t* outNext)
{
    InterlockedIncrement(&g_checks);
    if (!AdvanceReadableHead(head, sentinel, outNext))
    {
        InterlockedIncrement(&g_exhausted);
        return 0;
    }
    return 1;
}

// The original frame already saved ESI, EBP and EDI. Both continuations use
// that same frame; CheckHead updates pushad's saved EAX slot when readable.
__declspec(naked) void HeadThunk()
{
    __asm {
        pushfd
        pushad
        lea eax, [esp + 28]
        push eax
        push edi
        push dword ptr [edi]
        call CheckHead
        add esp, 12
        test eax, eax
        jz exhausted
        popad
        popfd
        jmp dword ptr [g_resume]
    exhausted:
        popad
        popfd
        jmp dword ptr [g_empty]
    }
}

void InstallChecked()
{
    if (!RegistrarBytesMatch())
    {
        InterlockedExchange(&g_state, sacs::PatchBuildMismatch);
        return;
    }
    if (!SiteDigestMatches(kLoop, kLoopLength, kLoopDigest)
        || !SiteDigestMatches(g_empty, kEmptyLength, kEmptyDigest))
    {
        InterlockedExchange(&g_state, sacs::PatchSiteMismatch);
        return;
    }
    unsigned char jump[kLoopLength] = {0xe9};
    uint32_t displacement = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(&HeadThunk) - kLoop - sizeof(jump));
    std::memcpy(jump + 1, &displacement, sizeof(displacement));
    void* at = reinterpret_cast<void*>(kLoop);
    DWORD old = 0;
    if (!VirtualProtect(at, sizeof(jump), PAGE_EXECUTE_READWRITE, &old))
    {
        InterlockedExchange(&g_state, sacs::PatchProtectFailed);
        return;
    }
    std::memcpy(at, jump, sizeof(jump));
    DWORD unused = 0;
    VirtualProtect(at, sizeof(jump), old, &unused);
    FlushInstructionCache(GetCurrentProcess(), at, sizeof(jump));
    InterlockedExchange(&g_state, sacs::PatchInstalled);
}
}  // namespace

void Install(HMODULE)
{
    wchar_t value[8];
    if (GetEnvironmentVariableW(L"SIMLINK_SEQUENCE_GUARD", value, 8) == 1 && value[0] == L'0')
    {
        return;
    }
    if ((sacs::PatchStateWord() & 0xff) == sacs::PatchKillSwitch)
    {
        InterlockedExchange(&g_state, sacs::PatchKillSwitch);
        return;
    }
    __try
    {
        InstallChecked();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        InterlockedExchange(&g_state, sacs::PatchException);
    }
}

int __cdecl Counter(int which)
{
    switch (which)
    {
    case 0: return static_cast<int>(g_state);
    case 1: return static_cast<int>(g_checks);
    case 2: return static_cast<int>(g_exhausted);
    default: return -1;
    }
}
}  // namespace sequenceguard
}  // namespace simlink
