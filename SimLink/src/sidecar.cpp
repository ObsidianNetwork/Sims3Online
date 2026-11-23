// SimLink sidecar (ASI plugin, TS3W.exe only). At load it creates its shared-memory section and two events and
// does nothing else: it never touches Mono on its own. When the controller (outside the game, after the bridge is
// up, so Mono is initialised) writes StateRegisterRequested and signals .control, this thread checks the exact bytes
// of mono_add_internal_call in this TS3W.exe build and that Mono's internal-call table exists, then registers:
//
//   int SimsOnlineServerHost.SimLinkNative::Version()
//   int SimsOnlineServerHost.SimLinkNative::Probe(string text, int length)
//   int SimsOnlineServerHost.SimLinkNative::Publish(int lane, int seq, string line, int length)
//   int SimsOnlineServerHost.SimLinkNative::InboundPoll()
//   int SimsOnlineServerHost.SimLinkNative::NoteBridge(int seq, string line, int length)
//   int SimsOnlineServerHost.SimLinkNative::ProbeArray(char[] probe, int length)
//   int SimsOnlineServerHost.SimLinkNative::InboundRead(char[] buffer, int capacity)
//   int SimsOnlineServerHost.SimLinkNative::SacsReadSeed(uint driverHandle, int which)          (native version 2)
//   int SimsOnlineServerHost.SimLinkNative::SacsWriteSeeds(uint driverHandle, int ctx, int spare)  (native version 2)
//   int SimsOnlineServerHost.SimLinkNative::SacsSetSwitches(int mask)                         (native version 3)
//   int SimsOnlineServerHost.SimLinkNative::SacsPatchState()                                  (native version 3)
//   int SimsOnlineServerHost.SimLinkNative::SacsPickCount()                                   (native version 3)
//   int SimsOnlineServerHost.SimLinkNative::SacsPickWord(int seq, int word)                   (native version 3)
//   int SimsOnlineServerHost.SimLinkNative::SacsPickCounter(int which)                        (native version 3)
//   int SimsOnlineServerHost.SimLinkNative::SacsOrderCount()                                  (native version 4)
//   int SimsOnlineServerHost.SimLinkNative::SacsOrderWord(int seq, int word)                  (native version 4)
//   int SimsOnlineServerHost.SimLinkNative::SacsOrderCounter(int which)                       (native version 4)
//   int SimsOnlineServerHost.SimLinkNative::SacsRandomCounter(int which)                      (native version 5)
//   int SimsOnlineServerHost.SimLinkNative::SacsRandomCount()                                 (native version 6)
//   int SimsOnlineServerHost.SimLinkNative::SacsRandomWord(int seq, int word)                 (native version 6)
//   int SimsOnlineServerHost.SimLinkNative::SacsGcCounter(int which)                          (native version 7)
//   int SimsOnlineServerHost.SimLinkNative::SacsGcInterval(int ms)                            (native version 7)
//   int SimsOnlineServerHost.SimLinkNative::SacsFreeRing(int which)                           (native version 8)
//   int SimsOnlineServerHost.SimLinkNative::SacsChunkReserve(int which)                       (native version 10)
//   int SimsOnlineServerHost.SimLinkNative::SacsOnScreen(int which)                           (native version 12)
//   int SimsOnlineServerHost.SimLinkNative::SacsWalkerRead(uint driverHandle, int index)       (native version 13)
//   int SimsOnlineServerHost.SimLinkNative::SacsWalkerWord(int word)                          (native version 13)
//   int SimsOnlineServerHost.SimLinkNative::SacsWalkerWrite(uint driverHandle, int index, int spare, int generation,
//                                                           int draws, int boundaries, int executions)  (native version 13)
//   int SimsOnlineServerHost.SimLinkNative::SacsWalkerCarry(uint oldHandle, uint newHandle, int spare)  (native version 13)
//   int SimsOnlineServerHost.SimLinkNative::SacsWalkerCounter(int which)                      (native version 13)
//   int SimsOnlineServerHost.SimLinkNative::SacsGcHeap(int which)                             (native version 14)
//
// and writes StateRegistered. Only then may the controller tell the mod to call them (SACS_RECORDER sidecar-on):
// an unregistered internal call is fatal in this Mono. Any mismatch fails closed (StateFailed + Error).
//
// Addresses and site digests correspond to Steam 1.67.2.024037 TS3W.exe (image base 0x400000, no ASLR):
// AddMonoInternalCall (RVA 0xa82550, cdecl) takes the loader lock, inserts into the table whose pointer lives at
// 0x11F436C, and unlocks. The managed string layout is not assumed: Probe finds it at runtime from a known string.
//
// Separately, at load (DllMain, TS3W.exe only) it installs tier 3's code patches (sacs_patch.cpp), which are
// digest-checked against this build and do nothing different until the mod switches them on (site A' included), and
// The GC rate gate (gc_gate.cpp): opt-in (SIMLINK_GCGATE=1, the server game only), checked and installed on
// its own, a pass-through until switch bit 5; and the explicit-free ring (free_ring.cpp): opt-in
// (SIMLINK_FREERING=1, the server game only), checked and installed on its own, recording every GC_free from then on;
// and the chunk reserve (chunk_reserve.cpp): on by default in every game (SIMLINK_CHUNKRESERVE=0 turns it off),
// three byte-checked rewrites, all or none, so that no interpreter frame ends on a stack chunk's GC descriptor;
// and the on-screen walk (onscreen.cpp): on by default in every game (SIMLINK_ONSCREEN=0 turns it off), one
// byte-checked rewrite so that the route follower's on-screen test answers true and every Sim walks the animated way;
// and the GC heap lever (gc_heap.cpp): on by default from native version 15 at 352 MB and 32 MB (SIMLINK_GCHEAP=0
// turns it off, SIMLINK_GCHEAP=<initialMB>,<growthMB> sets other values), four byte-checked immediates of
// Boehm's initial heap and growth step, all or none, before GC_init runs; with SIMLINK_GCFORCED=1, on its own, a timer
// on the forced collections EA's GC_collect_or_expand runs instead of growing the heap.
#include <windows.h>

#include <cstdio>
#include <cstring>

#include "chunk_reserve.h"
#include "onscreen.h"
#include "free_ring.h"
#include "gc_heap.h"
#include "gc_gate.h"
#include "ring.h"
#include "sacs_patch.h"
#include "site_digest.h"

namespace
{
using simlink::Ring;
namespace off = simlink::off;

constexpr uintptr_t kRegistrarVa = 0x00E82550;
constexpr uintptr_t kIcallTableVa = 0x011F436C;
constexpr size_t kRegistrarLength = 28;
constexpr simlink::SiteDigest kRegistrarDigest = {0x4B2B84492BF2330BULL, 0xCD4EDA16D46EFEDEULL};  // mono_add_internal_call entry check (28 bytes)
constexpr wchar_t kProbeText[] = L"SimLink-probe";
constexpr int kProbeLength = 13;

typedef void(__cdecl* AddInternalCallFn)(const char* name, const void* method);

Ring g_ring;    // game -> controller (server shadow lines, client shadow samples)
Ring g_inRing;  // controller -> game ("<section>.in": the client controller's copy of the server's lines)
HANDLE g_controlEvent = nullptr;
HANDLE g_dataEvent = nullptr;
volatile LONG g_stringLengthOffset = 0;  // 0 until Probe succeeds
volatile LONG g_consuming = 0;           // the inbound ring has one consumer: the game thread
volatile LONG g_arrayLengthOffset = 0;   // char[]: offset of max_length (0 until ProbeArray succeeds)
volatile LONG g_arrayDataOffset = 0;     // char[]: offset of the first element
constexpr wchar_t kArrayProbeText[] = L"SimLink-array!";
constexpr int kArrayProbeLength = 14;
constexpr uint16_t kReadSampleLane = 4;

// Client shadow: what the game thread saw of each inbound line, by seq. Compared when the same line arrives through
// the bridge (SACS_REPLAY). FNV-1a of the UTF-16 payload stands in for the payload.
struct InboundEntry
{
    int32_t seq;
    uint32_t length;
    uint64_t hash;
    int64_t publishQpc;  // the client controller's QueryPerformanceCounter when it received the line
    int64_t seenQpc;     // the game thread's, when it drained the line from the ring
};
constexpr uint32_t kInboundTableSize = 65536;  // a power of two; seq & (size - 1)
InboundEntry g_inbound[kInboundTableSize];
constexpr uint16_t kSampleLane = 3;
enum SampleResult
{
    SampleMatch = 1,
    SampleMismatch = 0,
    SampleNotInRing = 2,
};

uint64_t Fnv1a(const wchar_t* chars, uint32_t count)
{
    uint64_t hash = 1469598103934665603ull;
    const unsigned char* bytes = reinterpret_cast<const unsigned char*>(chars);
    for (uint32_t i = 0; i < count * 2u; ++i)
    {
        hash ^= bytes[i];
        hash *= 1099511628211ull;
    }
    return hash;
}

int OnInbound(void* context, uint16_t, int32_t seq, const wchar_t* chars, uint32_t count, int64_t qpc)
{
    const int64_t now = *static_cast<const int64_t*>(context);
    InboundEntry& entry = g_inbound[static_cast<uint32_t>(seq) & (kInboundTableSize - 1u)];
    entry.seq = seq;
    entry.length = count;
    entry.hash = Fnv1a(chars, count);
    entry.publishQpc = qpc;
    entry.seenQpc = now;
    return 1;
}

int DrainInbound(int64_t now)
{
    if (g_inRing.base == nullptr || InterlockedCompareExchange(&g_consuming, 1, 0) != 0)
    {
        return 0;
    }

    const int count = simlink::Consume(g_inRing, &OnInbound, &now);
    InterlockedExchange(&g_consuming, 0);
    return count;
}

int32_t ReadInt32(const unsigned char* at)
{
    int32_t value;
    std::memcpy(&value, at, 4);
    return value;
}

// ---- internal calls (cdecl, int and pointer arguments, int results) ----

int __cdecl SimLinkVersion()
{
    return simlink::kNativeVersion;
}

int __cdecl SimLinkProbe(const unsigned char* text, int length)
{
    int found = -1;
    if (text != nullptr && length == kProbeLength)
    {
        found = -2;
        const int candidates[] = {4, 8, 12};
        for (int candidate : candidates)
        {
            if (ReadInt32(text + candidate) == length &&
                std::memcmp(text + candidate + 4, kProbeText, kProbeLength * sizeof(wchar_t)) == 0)
            {
                found = candidate;
                InterlockedExchange(&g_stringLengthOffset, candidate);
                simlink::Store32(g_ring.base, off::StringLengthOffset, static_cast<uint32_t>(candidate));
                break;
            }
        }
    }

    simlink::Store32(g_ring.base, off::ProbeResult, static_cast<uint32_t>(found));
    return found;
}

int __cdecl SimLinkPublish(int lane, int seq, const unsigned char* line, int length)
{
    const LONG offset = g_stringLengthOffset;
    if (offset == 0 || line == nullptr || length < 0 || ReadInt32(line + offset) != length)
    {
        simlink::Increment64(g_ring.base, off::BadArgs);
        return 0;
    }

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const int written = simlink::Publish(g_ring, static_cast<uint16_t>(lane), seq,
                                         reinterpret_cast<const wchar_t*>(line + offset + 4),
                                         static_cast<uint32_t>(length), now.QuadPart);
    if (written)
    {
        SetEvent(g_dataEvent);
    }

    return written;
}

// Client shadow, called on the game thread at each bridge pump tick: stamps when the inbound lines became visible
// to the game thread. Returns how many were drained.
int __cdecl SimLinkInboundPoll()
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    return DrainInbound(now.QuadPart);
}

// Client shadow, called on the game thread for each line of a SACS_REPLAY batch (the bridge route): compares it
// with what the inbound ring delivered for the same seq and publishes one sample on lane 3 of the outbound ring:
// payload "seq|publishQpc|seenQpc|result", qpc = now (bridge arrival). Returns the result (1 match, 0 mismatch,
// 2 not in the ring), or -1 for bad arguments.
int __cdecl SimLinkNoteBridge(int seq, const unsigned char* line, int length)
{
    const LONG offset = g_stringLengthOffset;
    if (offset == 0 || line == nullptr || length < 0 || ReadInt32(line + offset) != length)
    {
        simlink::Increment64(g_ring.base, off::BadArgs);
        return -1;
    }

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    DrainInbound(now.QuadPart);
    const wchar_t* chars = reinterpret_cast<const wchar_t*>(line + offset + 4);
    const InboundEntry& entry = g_inbound[static_cast<uint32_t>(seq) & (kInboundTableSize - 1u)];
    int result = SampleNotInRing;
    int64_t publishQpc = 0;
    int64_t seenQpc = 0;
    if (entry.seq == seq)
    {
        result = entry.length == static_cast<uint32_t>(length) && entry.hash == Fnv1a(chars, static_cast<uint32_t>(length))
                     ? SampleMatch
                     : SampleMismatch;
        publishQpc = entry.publishQpc;
        seenQpc = entry.seenQpc;
    }

    wchar_t sample[96];
    const int count = swprintf_s(sample, L"%d|%lld|%lld|%d", seq, publishQpc, seenQpc, result);
    if (count > 0)
    {
        simlink::Publish(g_ring, kSampleLane, seq, sample, static_cast<uint32_t>(count), now.QuadPart);
        SetEvent(g_dataEvent);
    }

    return result;
}

// Client ring path: finds where this runtime keeps a char[]'s length and elements, from a known array. Returns the
// element offset (> 0), or < 0.
int __cdecl SimLinkProbeArray(const unsigned char* array, int length)
{
    int found = -1;
    if (array != nullptr && length == kArrayProbeLength)
    {
        found = -2;
        const int lengthCandidates[] = {4, 8, 12, 16};
        for (int lengthOffset : lengthCandidates)
        {
            if (ReadInt32(array + lengthOffset) != length)
            {
                continue;
            }

            const int dataCandidates[] = {lengthOffset + 4, lengthOffset + 8};
            for (int dataOffset : dataCandidates)
            {
                if (std::memcmp(array + dataOffset, kArrayProbeText, kArrayProbeLength * sizeof(wchar_t)) == 0)
                {
                    InterlockedExchange(&g_arrayLengthOffset, lengthOffset);
                    InterlockedExchange(&g_arrayDataOffset, dataOffset);
                    found = dataOffset;
                    break;
                }
            }

            if (found > 0)
            {
                break;
            }
        }
    }

    return found;
}

// Client ring path, on the game thread: copies the next batch of inbound lines ("seq|kind|...", each followed by
// '\n'; the controller ends each SACS_REPLAY-sized batch with a marker) into buffer (a char[] of `capacity` elements,
// checked against the array's own length) and consumes it. Returns the chars written, 0 when the ring is empty, or
// -(chars needed) when the first line doesn't fit (grow and retry). Publishes one sample per read on lane 4:
// "records|oldestQpc|newestQpc", qpc = now.
int __cdecl SimLinkInboundRead(unsigned char* buffer, int capacity)
{
    const LONG lengthOffset = g_arrayLengthOffset;
    const LONG dataOffset = g_arrayDataOffset;
    if (lengthOffset == 0 || buffer == nullptr || capacity <= 0 || ReadInt32(buffer + lengthOffset) != capacity)
    {
        simlink::Increment64(g_ring.base, off::BadArgs);
        return 0;
    }

    if (g_inRing.base == nullptr || InterlockedCompareExchange(&g_consuming, 1, 0) != 0)
    {
        return 0;
    }

    const simlink::BatchRead read =
        simlink::ReadBatch(g_inRing, reinterpret_cast<wchar_t*>(buffer + dataOffset), static_cast<uint32_t>(capacity));
    InterlockedExchange(&g_consuming, 0);
    if (read.records == 0)
    {
        return read.needFirst > 0 ? -read.needFirst : 0;
    }

    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    wchar_t sample[96];
    const int count = swprintf_s(sample, L"%d|%lld|%lld", read.records, read.oldest, read.newest);
    if (count > 0)
    {
        simlink::Publish(g_ring, kReadSampleLane, read.records, sample, static_cast<uint32_t>(count), now.QuadPart);
        SetEvent(g_dataEvent);
    }

    return static_cast<int>(read.used);
}

// ---- tier 1: SACS driver seeds ----
//
// A script driver handle resolves to the native driver the way the game's own SACS_* internal calls do
// (ICALL_SACS_RequestStateImpl at 0x767440): the script host singleton (0x7692B0 reads it from 0x11D4BBC), then the
// host's virtual at +0xCC (thiscall, the handle). The result counts as a driver only if its vtable is the driver's
// (0x1020110, installed by both constructors) and its decision-graph context (+0x44) points back at it. Both seeds are
// multiplicative LCG states that the game reseeds from rdtsc when they are 0: +0xA8 (context +0x64) is what the jazz
// Random node draws from (0x4FA870), +0x34 is a second state no SACS code draws from (tier 3's key). The two code sites
// the resolution relies on are byte-checked once; a mismatch makes both calls fail closed.
constexpr uintptr_t kHostGetterVa = 0x007692B0;
constexpr size_t kHostGetterLength = 6;
constexpr simlink::SiteDigest kHostGetterDigest = {0xB8EA0ED44CD6AD76ULL, 0x050D6665AA8C0586ULL};  // script host getter check (6 bytes)
constexpr uintptr_t kResolveUseVa = 0x0076744D;
constexpr size_t kResolveUseLength = 13;
constexpr simlink::SiteDigest kResolveUseDigest = {0xA805DC81245AF6ABULL, 0x2CF92056E05F3BDFULL};  // host vtable +0xCC resolve call check (13 bytes)
constexpr uintptr_t kScriptHostVa = 0x011D4BBC;
constexpr uint32_t kDriverVtable = 0x01020110;
constexpr size_t kHostResolveSlot = 0xCC / sizeof(void*);
constexpr size_t kDriverContext = 0x44;
constexpr size_t kDriverContextSeed = 0xA8;
constexpr size_t kDriverSpareSeed = 0x34;

enum SeedResult
{
    SeedNoDriver = 0,
    SeedWritten = 1,
    SeedBytesMismatch = -1,
    SeedException = -2,
    SeedReadBackDiffers = -3,
};

volatile LONG g_seedSitesChecked = 0;  // 0 not yet, 1 match, -1 mismatch

typedef void*(__fastcall* ResolveDriverFn)(void* host, void* unusedEdx, uint32_t handle);  // thiscall, callee cleans

bool SeedSitesMatch()
{
    LONG checked = g_seedSitesChecked;
    if (checked == 0)
    {
        const bool match =
            simlink::SiteDigestMatches(kHostGetterVa, kHostGetterLength, kHostGetterDigest) &&
            simlink::SiteDigestMatches(kResolveUseVa, kResolveUseLength, kResolveUseDigest);
        checked = match ? 1 : -1;
        InterlockedExchange(&g_seedSitesChecked, checked);
    }

    return checked == 1;
}

unsigned char* ResolveDriver(uint32_t handle)
{
    void* host = *reinterpret_cast<void* volatile*>(kScriptHostVa);
    if (host == nullptr)
    {
        return nullptr;
    }

    void** vtable = *static_cast<void***>(host);
    const ResolveDriverFn resolve = reinterpret_cast<ResolveDriverFn>(vtable[kHostResolveSlot]);
    unsigned char* driver = static_cast<unsigned char*>(resolve(host, nullptr, handle));
    if (driver == nullptr || *reinterpret_cast<const uint32_t*>(driver) != kDriverVtable ||
        *reinterpret_cast<unsigned char* const*>(driver + kDriverContext) != driver)
    {
        return nullptr;
    }

    simlink::sacs::NoteResolved(handle, reinterpret_cast<uintptr_t>(driver));  // walker-copy carry (native version 13)
    return driver;
}

// which: 0 = the Random-node seed (driver +0xA8), 1 = the spare seed (+0x34). Returns the seed, or 0 when the handle
// does not resolve to a driver or the code sites differ (0 is also what the game treats as "reseed").
int __cdecl SimLinkSacsReadSeed(int handle, int which)
{
    __try
    {
        if (!SeedSitesMatch())
        {
            return 0;
        }

        const unsigned char* driver = ResolveDriver(static_cast<uint32_t>(handle));
        if (driver == nullptr)
        {
            return 0;
        }

        return *reinterpret_cast<const volatile int32_t*>(driver + (which == 1 ? kDriverSpareSeed : kDriverContextSeed));
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}

// Writes the non-zero seeds into the handle's driver and reads them back. Returns a SeedResult.
int __cdecl SimLinkSacsWriteSeeds(int handle, int contextSeed, int spareSeed)
{
    __try
    {
        if (!SeedSitesMatch())
        {
            return SeedBytesMismatch;
        }

        unsigned char* driver = ResolveDriver(static_cast<uint32_t>(handle));
        if (driver == nullptr)
        {
            return SeedNoDriver;
        }

        volatile int32_t* context = reinterpret_cast<volatile int32_t*>(driver + kDriverContextSeed);
        volatile int32_t* spare = reinterpret_cast<volatile int32_t*>(driver + kDriverSpareSeed);
        if (contextSeed != 0)
        {
            *context = contextSeed;
        }

        if (spareSeed != 0)
        {
            *spare = spareSeed;
        }

        return (contextSeed == 0 || *context == contextSeed) && (spareSeed == 0 || *spare == spareSeed) ? SeedWritten
                                                                                                         : SeedReadBackDiffers;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return SeedException;
    }
}

// ---- registration ----

uint32_t RegisterInternalCalls()
{
    __try
    {
        if (!simlink::SiteDigestMatches(kRegistrarVa, kRegistrarLength, kRegistrarDigest))
        {
            return simlink::ErrorRegistrarBytes;
        }

        if (*reinterpret_cast<void* volatile*>(kIcallTableVa) == nullptr)
        {
            return simlink::ErrorIcallTableNull;
        }

        AddInternalCallFn add = reinterpret_cast<AddInternalCallFn>(kRegistrarVa);
        add("SimsOnlineServerHost.SimLinkNative::Version", reinterpret_cast<const void*>(&SimLinkVersion));
        add("SimsOnlineServerHost.SimLinkNative::Probe", reinterpret_cast<const void*>(&SimLinkProbe));
        add("SimsOnlineServerHost.SimLinkNative::Publish", reinterpret_cast<const void*>(&SimLinkPublish));
        add("SimsOnlineServerHost.SimLinkNative::InboundPoll", reinterpret_cast<const void*>(&SimLinkInboundPoll));
        add("SimsOnlineServerHost.SimLinkNative::NoteBridge", reinterpret_cast<const void*>(&SimLinkNoteBridge));
        add("SimsOnlineServerHost.SimLinkNative::ProbeArray", reinterpret_cast<const void*>(&SimLinkProbeArray));
        add("SimsOnlineServerHost.SimLinkNative::InboundRead", reinterpret_cast<const void*>(&SimLinkInboundRead));
        add("SimsOnlineServerHost.SimLinkNative::SacsReadSeed", reinterpret_cast<const void*>(&SimLinkSacsReadSeed));
        add("SimsOnlineServerHost.SimLinkNative::SacsWriteSeeds", reinterpret_cast<const void*>(&SimLinkSacsWriteSeeds));
        add("SimsOnlineServerHost.SimLinkNative::SacsSetSwitches", reinterpret_cast<const void*>(&simlink::sacs::SetSwitches));
        add("SimsOnlineServerHost.SimLinkNative::SacsPatchState", reinterpret_cast<const void*>(&simlink::sacs::PatchStateIcall));
        add("SimsOnlineServerHost.SimLinkNative::SacsPickCount", reinterpret_cast<const void*>(&simlink::sacs::PickCount));
        add("SimsOnlineServerHost.SimLinkNative::SacsPickWord", reinterpret_cast<const void*>(&simlink::sacs::PickWord));
        add("SimsOnlineServerHost.SimLinkNative::SacsPickCounter", reinterpret_cast<const void*>(&simlink::sacs::PickCounter));
        add("SimsOnlineServerHost.SimLinkNative::SacsOrderCount", reinterpret_cast<const void*>(&simlink::sacs::OrderCount));
        add("SimsOnlineServerHost.SimLinkNative::SacsOrderWord", reinterpret_cast<const void*>(&simlink::sacs::OrderWord));
        add("SimsOnlineServerHost.SimLinkNative::SacsOrderCounter", reinterpret_cast<const void*>(&simlink::sacs::OrderCounter));
        add("SimsOnlineServerHost.SimLinkNative::SacsRandomCounter", reinterpret_cast<const void*>(&simlink::sacs::RandomCounter));
        add("SimsOnlineServerHost.SimLinkNative::SacsRandomCount", reinterpret_cast<const void*>(&simlink::sacs::RandomLogCount));
        add("SimsOnlineServerHost.SimLinkNative::SacsRandomWord", reinterpret_cast<const void*>(&simlink::sacs::RandomLogWord));
        add("SimsOnlineServerHost.SimLinkNative::SacsGcCounter", reinterpret_cast<const void*>(&simlink::gc::GateCounter));
        add("SimsOnlineServerHost.SimLinkNative::SacsGcInterval", reinterpret_cast<const void*>(&simlink::gc::GateInterval));
        add("SimsOnlineServerHost.SimLinkNative::SacsFreeRing", reinterpret_cast<const void*>(&simlink::freering::Counter));
        add("SimsOnlineServerHost.SimLinkNative::SacsChunkReserve", reinterpret_cast<const void*>(&simlink::chunkreserve::Counter));
        add("SimsOnlineServerHost.SimLinkNative::SacsOnScreen", reinterpret_cast<const void*>(&simlink::onscreen::Counter));
        add("SimsOnlineServerHost.SimLinkNative::SacsWalkerRead", reinterpret_cast<const void*>(&simlink::sacs::WalkerRead));
        add("SimsOnlineServerHost.SimLinkNative::SacsWalkerWord", reinterpret_cast<const void*>(&simlink::sacs::WalkerWord));
        add("SimsOnlineServerHost.SimLinkNative::SacsWalkerWrite", reinterpret_cast<const void*>(&simlink::sacs::WalkerWrite));
        add("SimsOnlineServerHost.SimLinkNative::SacsWalkerCarry", reinterpret_cast<const void*>(&simlink::sacs::WalkerCarry));
        add("SimsOnlineServerHost.SimLinkNative::SacsWalkerCounter", reinterpret_cast<const void*>(&simlink::sacs::WalkerCounter));
        add("SimsOnlineServerHost.SimLinkNative::SacsGcHeap", reinterpret_cast<const void*>(&simlink::gcheap::Counter));
        return simlink::ErrorNone;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return simlink::ErrorException;
    }
}

bool IsGameProcess()
{
    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
    const wchar_t* name = length > 0 ? wcsrchr(path, L'\\') : nullptr;
    return name != nullptr && _wcsicmp(name + 1, L"TS3W.exe") == 0;
}

DWORD WINAPI SidecarMain(LPVOID)
{
    if (!IsGameProcess())
    {
        return 0;
    }

    const DWORD pid = GetCurrentProcessId();
    wchar_t name[96];
    swprintf_s(name, L"Local\\SimsOnline.SimLink.%lu", pid);
    HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                        simlink::kHeaderSize + simlink::kDefaultRingSize, name);
    if (section == nullptr)
    {
        return 0;
    }

    unsigned char* base = static_cast<unsigned char*>(MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    if (base == nullptr)
    {
        return 0;
    }

    simlink::InitSection(g_ring, base, simlink::kDefaultRingSize, pid, "SimLink 1 (shadow) " __DATE__ " " __TIME__);
    simlink::Store32(base, off::PatchState, static_cast<uint32_t>(simlink::sacs::PatchStateWord()));

    // The inbound lane: the controller writes, the game thread reads (client shadow). Same layout; its own header.
    wchar_t inName[112];
    swprintf_s(inName, L"%s.in", name);
    HANDLE inSection = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                          simlink::kHeaderSize + simlink::kDefaultRingSize, inName);
    unsigned char* inBase = inSection != nullptr
                                ? static_cast<unsigned char*>(MapViewOfFile(inSection, FILE_MAP_ALL_ACCESS, 0, 0, 0))
                                : nullptr;
    if (inBase != nullptr)
    {
        simlink::InitSection(g_inRing, inBase, simlink::kDefaultRingSize, pid, "SimLink 1 inbound");
    }

    wchar_t eventName[128];
    swprintf_s(eventName, L"%s.control", name);
    g_controlEvent = CreateEventW(nullptr, FALSE, FALSE, eventName);
    swprintf_s(eventName, L"%s.data", name);
    g_dataEvent = CreateEventW(nullptr, FALSE, FALSE, eventName);
    if (g_controlEvent == nullptr || g_dataEvent == nullptr)
    {
        return 0;
    }

    for (;;)
    {
        WaitForSingleObject(g_controlEvent, INFINITE);
        const uint32_t state = simlink::Load32(base, off::State);
        if (state == simlink::StateRegisterRequested)
        {
            const uint32_t error = RegisterInternalCalls();
            simlink::Store32(base, off::Error, error);
            simlink::Store32(base, off::State, error == simlink::ErrorNone ? simlink::StateRegistered : simlink::StateFailed);
        }
    }
}
}  // namespace

namespace simlink
{
// The build pin, shared with the tier 3 patches: mono_add_internal_call's digest in this TS3W.exe build.
bool RegistrarBytesMatch()
{
    __try
    {
        return SiteDigestMatches(kRegistrarVa, kRegistrarLength, kRegistrarDigest);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// Resolve a script driver handle as the seed calls do. 1 with *driver set, 0 no driver,
// -1 this build's code sites differ, -2 exception.
int ResolveSeedDriver(uint32_t handle, unsigned char** driver)
{
    *driver = nullptr;
    __try
    {
        if (!SeedSitesMatch())
        {
            return -1;
        }

        *driver = ResolveDriver(handle);
        return *driver != nullptr ? 1 : 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *driver = nullptr;
        return -2;
    }
}
}  // namespace simlink

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        if (IsGameProcess())
        {
            simlink::sacs::Install(module);  // before the game runs any SACS code
            simlink::gc::Install(module);    // with SIMLINK_GCGATE=1 only; before the frame loop first runs (the
                                             // rel32 it writes is unaligned)
            simlink::freering::Install(module);  // with SIMLINK_FREERING=1 only; before the game runs
            simlink::chunkreserve::Install(module);  // unless SIMLINK_CHUNKRESERVE=0; before the game runs
            simlink::onscreen::Install(module);      // unless SIMLINK_ONSCREEN=0; before the game routes a Sim
            simlink::gcheap::Install(module);        // pins (reads only); rows unless SIMLINK_GCHEAP=0, timer with
                                                     // SIMLINK_GCFORCED=1; before GC_init runs
        }

        HANDLE thread = CreateThread(nullptr, 0, SidecarMain, nullptr, 0, nullptr);
        if (thread != nullptr)
        {
            CloseHandle(thread);
        }
    }

    return TRUE;
}
