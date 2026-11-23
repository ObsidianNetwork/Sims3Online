// SimLink ring self-test (x86, outside the game): creates a section the way the sidecar does, under a given name and
// a small ring, and publishes `count` records (seq i, payload "i|T|" + i%97 x 'x'), retrying while the ring is full so
// a concurrent reader forces wrap-arounds. Sets NativeVersion to -1 when done, then waits for the reader to finish.
//
//   simlink_selftest.exe <section-name> <count> <ring-bytes> <seconds-to-wait>
//   simlink_selftest.exe keyed      (the keyed Random draw's key and value against fixed vectors)
#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <string>

#include "chunk_reserve_logic.h"
#include "onscreen_logic.h"
#include "gc_heap_logic.h"
#include "walker_copy_logic.h"
#include "free_ring_logic.h"
#include "gc_gate_logic.h"
#include "ring.h"
#include "sacs_pick_logic.h"
#include "sacs_random.h"
#include "site_digest.h"

struct ConsumeCheck
{
    int expected = 1;   // next seq
    int bad = 0;
    int limit = 0;      // > 0: stop after this many records per Consume call (resumed by the next call)
    int thisCall = 0;
};

int OnConsumed(void* context, uint16_t lane, int32_t seq, const wchar_t* chars, uint32_t count, int64_t)
{
    ConsumeCheck& check = *static_cast<ConsumeCheck*>(context);
    if (check.limit > 0 && check.thisCall >= check.limit)
    {
        return 0;
    }
    ++check.thisCall;
    std::wstring want = std::to_wstring(seq) + L"|C|" + std::wstring(static_cast<size_t>(seq % 89), L'y');
    if (lane != 1 || seq != check.expected || want.size() != count || std::wmemcmp(want.c_str(), chars, count) != 0)
    {
        ++check.bad;
    }
    check.expected = seq + 1;
    return 1;
}

// simlink_selftest.exe consume <section-name> <count> <ring-bytes> <seconds>: the reverse direction. Creates the
// section, prints "ready", then drains what another process writes (seq i, payload "i|C|" + i%89 x 'y') with the
// native consumer, and prints "consumed=N bad=M" (exit 0 when all arrived in order and intact).
int Consumer(const wchar_t* name, int count, uint32_t ringSize, int seconds, int limit)
{
    HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, simlink::kHeaderSize + ringSize, name);
    if (section == nullptr)
    {
        return 1;
    }

    unsigned char* base = static_cast<unsigned char*>(MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    simlink::Ring ring;
    simlink::InitSection(ring, base, ringSize, GetCurrentProcessId(), "SimLink selftest consumer");
    std::printf("ready\n");
    std::fflush(stdout);
    ConsumeCheck check;
    check.limit = limit;
    int consumed = 0;
    const ULONGLONG deadline = GetTickCount64() + static_cast<ULONGLONG>(seconds) * 1000u;
    while (consumed < count && GetTickCount64() < deadline)
    {
        check.thisCall = 0;
        const int got = simlink::Consume(ring, &OnConsumed, &check);
        if (got < 0)
        {
            ++check.bad;
            break;
        }

        consumed += got;
        if (got == 0)
        {
            Sleep(0);
        }
    }

    std::printf("consumed=%d bad=%d\n", consumed, check.bad);
    std::fflush(stdout);
    return consumed == count && check.bad == 0 ? 0 : 1;
}

// simlink_selftest.exe consume-batches <section-name> <batches> <ring-bytes> <seconds>: the client ring path's reader.
// Another process writes batches k = 1..N of (k % 5) + 1 lines "k|B|i" followed by a batch-end marker; each ReadBatch
// must return exactly one whole batch, in order. Prints "batches=N bad=M".
int BatchConsumer(const wchar_t* name, int batches, uint32_t ringSize, int seconds)
{
    HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, simlink::kHeaderSize + ringSize, name);
    if (section == nullptr)
    {
        return 1;
    }

    unsigned char* base = static_cast<unsigned char*>(MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    simlink::Ring ring;
    simlink::InitSection(ring, base, ringSize, GetCurrentProcessId(), "SimLink selftest batch consumer");
    std::printf("ready\n");
    std::fflush(stdout);
    static wchar_t buffer[65536];
    int got = 0;
    int bad = 0;
    const ULONGLONG deadline = GetTickCount64() + static_cast<ULONGLONG>(seconds) * 1000u;
    while (got < batches && GetTickCount64() < deadline)
    {
        const simlink::BatchRead read = simlink::ReadBatch(ring, buffer, 65536);
        if (read.records == 0)
        {
            Sleep(0);
            continue;
        }

        ++got;
        std::wstring want;
        const int lines = (got % 5) + 1;
        for (int i = 1; i <= lines; ++i)
        {
            want += std::to_wstring(got) + L"|B|" + std::to_wstring(i) + L"\n";
        }
        if (read.records != lines || want.size() != read.used || std::wmemcmp(want.c_str(), buffer, read.used) != 0)
        {
            if (bad == 0)
            {
                std::fwprintf(stderr, L"first bad batch %d: records=%d want=%d used=%u wantChars=%zu got='%.*s'\n", got,
                              read.records, lines, read.used, want.size(), static_cast<int>(read.used), buffer);
            }
            ++bad;
        }
    }

    std::printf("batches=%d bad=%d\n", got, bad);
    std::fflush(stdout);
    return got == batches && bad == 0 ? 0 : 1;
}

// simlink_selftest.exe claim-race: in-process. A batch whose records the writer claims while the consumer reads them
// (the client controller taking its backlog back) is not returned, the read position stays where the claim put it,
// and the next batch reads normally. Prints "claim-race ok" or the first failed check.
struct ClaimContext
{
    simlink::Ring* ring;
    bool claimed = false;
    int seen = 0;
};

int OnClaimedRecord(void* context, uint16_t, int32_t, const wchar_t*, uint32_t, int64_t)
{
    ClaimContext& claim = *static_cast<ClaimContext*>(context);
    ++claim.seen;
    if (!claim.claimed)
    {
        claim.claimed = true;  // the writer claims everything unread, mid-read
        simlink::Store64(claim.ring->base, simlink::off::ReadPos, simlink::Load64(claim.ring->base, simlink::off::WritePos));
    }
    return 1;
}

int ClaimRace()
{
    const uint32_t ringSize = 4096;
    static unsigned char memory[simlink::kHeaderSize + 4096];
    simlink::Ring ring;
    simlink::InitSection(ring, memory, ringSize, GetCurrentProcessId(), "SimLink selftest claim-race");
    const wchar_t* lines[] = {L"1|B|1", L"1|B|2", L"1|B|3"};
    for (const wchar_t* line : lines)
    {
        simlink::Publish(ring, simlink::kLaneReplayLine, 1, line, static_cast<uint32_t>(std::wcslen(line)), 1);
    }
    simlink::Publish(ring, simlink::kLaneBatchEnd, 0, L"", 0, 1);
    const uint64_t claimedAt = simlink::Load64(ring.base, simlink::off::WritePos);

    ClaimContext claim{&ring};
    const int consumed = simlink::Consume(ring, &OnClaimedRecord, &claim);
    if (consumed != simlink::kConsumeClaimed || claim.seen == 0)
    {
        std::printf("claim-race: consume returned %d after %d records, want %d\n", consumed, claim.seen, simlink::kConsumeClaimed);
        return 1;
    }
    if (simlink::Load64(ring.base, simlink::off::ReadPos) != claimedAt)
    {
        std::printf("claim-race: the read position moved off the claim\n");
        return 1;
    }

    static wchar_t buffer[256];
    simlink::BatchRead nothing = simlink::ReadBatch(ring, buffer, 256);
    if (nothing.records != 0)
    {
        std::printf("claim-race: %d claimed lines read again\n", nothing.records);
        return 1;
    }

    simlink::Publish(ring, simlink::kLaneReplayLine, 2, L"2|B|1", 5, 2);
    simlink::Publish(ring, simlink::kLaneBatchEnd, 0, L"", 0, 2);
    simlink::BatchRead next = simlink::ReadBatch(ring, buffer, 256);
    if (next.records != 1 || next.used != 6 || std::wmemcmp(buffer, L"2|B|1\n", 6) != 0)
    {
        std::printf("claim-race: the batch after the claim read %d records\n", next.records);
        return 1;
    }

    std::printf("claim-race ok\n");
    return 0;
}

// The vectors `python scripts/sacs_random.py vectors` prints (scripts/sacs_random_test.py checks the same numbers):
// the seven key fields, the key, and its top 24 bits (the value's numerator).
int KeyedVectors()
{
    struct Vector
    {
        uint32_t fields[7];
        uint64_t key;
        uint32_t top;
    };
    const Vector vectors[] = {
        {{0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u, 0x00000000u}, 0x87F927C63FEED619ull, 0x87F927u},
        {{0x97109628u, 0x00000000u, 0x48001C3Cu, 0x00000005u, 0x00000000u, 0x00000000u, 0x00000000u}, 0xAD40382421A32ECFull, 0xAD4038u},
        {{0x97109628u, 0x00000000u, 0x48001C3Cu, 0x00000005u, 0x00000000u, 0x00000000u, 0x00000001u}, 0xC6D3BAB941D2D781ull, 0xC6D3BAu},
        {{0x97109628u, 0x00000000u, 0x48001C3Cu, 0x00000005u, 0x00000000u, 0x00000001u, 0x00000000u}, 0x9DA48C52A36E1BD9ull, 0x9DA48Cu},
        {{0x97109628u, 0x00000000u, 0x48001C3Cu, 0x00000005u, 0x00000001u, 0x00000000u, 0x00000000u}, 0xD997A5A98E61F28Cull, 0xD997A5u},
        {{0x9E3F61E1u, 0x00000000u, 0x48001C3Cu, 0x00000005u, 0x00000000u, 0x00000000u, 0x00000000u}, 0x6DED4E737F2A52DEull, 0x6DED4Eu},
        {{0x9E3F61E1u, 0x00000001u, 0x48001C3Cu, 0x00000005u, 0x00000000u, 0x00000000u, 0x00000000u}, 0xE44E4F2E88EDEF6Bull, 0xE44E4Fu},
        {{0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu}, 0xEF0D594039C87523ull, 0xEF0D59u},
    };
    int bad = 0;
    for (const Vector& v : vectors)
    {
        const uint32_t* f = v.fields;
        const uint64_t key = simlink::sacs::RandomKey(f[0], f[1], f[2], f[3], f[4], f[5], f[6]);
        const float unit = simlink::sacs::KeyedUnit(key);
        if (key != v.key || unit != static_cast<float>(v.top) / 16777216.0f || !(unit >= 0.0f && unit < 1.0f))
        {
            std::printf("keyed: fields %08X %08X ... key %016llX, want %016llX\n", f[0], f[2], key, v.key);
            ++bad;
        }
    }

    // The largest value stays below 1, so the evaluate's scaled value never reaches the total.
    if (!(simlink::sacs::KeyedUnit(~0ull) < 1.0f))
    {
        std::printf("keyed: KeyedUnit(~0) is not below 1\n");
        ++bad;
    }

    if (bad != 0)
    {
        return 1;
    }

    std::printf("keyed ok\n");
    return 0;
}

// GC rate gate (gc_gate_logic.h): one frame's decision from the switch bit, GC_gc_no, GC_mark_state, the
// time and the interval, as the gate in gc_gate.cpp runs it.
int GcGateDecisions()
{
    using namespace simlink::gc;
    int bad = 0;
    auto expect = [&bad](const char* what, GateMemory& mem, const GateInput& in, Decision want, bool wantHeld) {
        bool held = false;
        const Decision got = Decide(mem, in, held);
        if (got != want || held != wantHeld)
        {
            std::printf("gcgate: %s: decision %d held %d, want %d held %d\n", what, got, held, want, wantHeld);
            ++bad;
        }
    };
    const uint32_t interval = 6000;
    const uint64_t ms = 1000;
    GateMemory m = {};

    // Bit off: every frame passes through, and the clock follows GC_gc_no.
    expect("off", m, {false, 10, 1, 0, interval}, DecisionPass, false);
    expect("off, a collection ends", m, {false, 11, 0, 4000 * ms, interval}, DecisionPass, false);
    // Bit on: the interval runs from GC_gc_no's last change (4 s), not from the first frame.
    expect("on, 5 s after the last collection", m, {true, 11, 0, 9000 * ms, interval}, DecisionSkip, false);
    expect("on, 6 s after", m, {true, 11, 0, 10000 * ms, interval}, DecisionAdmit, false);
    Admitted(m, 12, 10200 * ms);  // the admitted call ran a whole collection, ending at 10.2 s
    expect("on, just after an admitted call", m, {true, 12, 0, 10300 * ms, interval}, DecisionSkip, false);
    expect("on, 5.9 s after it", m, {true, 12, 0, 16100 * ms, interval}, DecisionSkip, false);
    // An allocation that collected (GC_gc_no moved without the gate) restarts the clock.
    expect("on, an allocation collected", m, {true, 13, 0, 16300 * ms, interval}, DecisionSkip, false);
    expect("on, 6 s after the allocation's", m, {true, 13, 0, 22300 * ms, interval}, DecisionAdmit, false);
    Admitted(m, 14, 22400 * ms);
    // A collection in progress after an admitted call: finished at once, and counted as held at entry.
    expect("on, a mark in progress", m, {true, 14, 1, 22500 * ms, interval}, DecisionAdmit, true);

    // A mark in progress when the bit comes on: finished at once, not counted (no admitted call yet).
    GateMemory fresh = {};
    expect("bit on mid-mark", fresh, {true, 20, 5, 100 * ms, interval}, DecisionAdmit, false);
    Admitted(fresh, 21, 150 * ms);
    // The bit off and on again starts afresh: a mark the game's own steps left in progress is not counted.
    expect("off again", fresh, {false, 21, 1, 200 * ms, interval}, DecisionPass, false);
    expect("on again mid-mark", fresh, {true, 21, 1, 300 * ms, interval}, DecisionAdmit, false);

    // Interval 0: every frame runs a whole collection.
    GateMemory zero = {};
    expect("interval 0", zero, {true, 1, 0, 5 * ms, 0}, DecisionAdmit, false);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("gcgate ok\n");
    return 0;
}

// The explicit-free ring (free_ring_logic.h): the callers' fields and frames, the slots, seq written last, the
// script thread and the rate, as free_ring.cpp's Note uses them. The layout itself is pinned by static_asserts in the
// header.
simlink::freering::Ring g_testRing;

int FreeRingLogic()
{
    using namespace simlink::freering;
    int bad = 0;
    auto expect = [&bad](const char* what, uint32_t got, uint32_t want) {
        if (got != want)
        {
            std::printf("freering: %s: %u, want %u\n", what, got, want);
            ++bad;
        }
    };

    // Every site is found, once; a return address that is not one has no site.
    for (const FreeSite& site : kFreeSites)
    {
        expect("site found", SiteFor(site.ret) == &site ? 1u : 0u, 1u);
    }
    expect("unknown caller", SiteFor(0x00E4BEB5) == nullptr ? 1u : 0u, 1u);

    // GC_free's entry stack: [0] return, [1] block, then the caller's pushes, its return and its arguments.
    const uint32_t stack[8] = {0, 0x5000, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24};
    expect("frame release: EBP", FrameFor(SiteFor(0x00E52E39), 0xF00, 0x5000, stack), 0xF00);
    expect("the frame's own free: the block", FrameFor(SiteFor(0x00E53EAA), 0xF00, 0x5000, stack), 0x5000);
    expect("0xD71B70's chunk: [esp+20]", FrameFor(SiteFor(0x00D71BCB), 0xF00, 0x5000, stack), 0x1C);
    expect("method exit: [esp+24]", FrameFor(SiteFor(0x00D71AB0), 0xF00, 0x5000, stack), 0x20);
    expect("method exit, second path: [esp+28]", FrameFor(SiteFor(0x00D71B4C), 0xF00, 0x5000, stack), 0x24);
    expect("0xE535E0's list: [esp+28]", FrameFor(SiteFor(0x00E53605), 0xF00, 0x5000, stack), 0x24);
    expect("allocator Free: none", FrameFor(SiteFor(0x00D70DCA), 0xF00, 0x5000, stack), 0);
    expect("unknown caller: none", FrameFor(nullptr, 0xF00, 0x5000, stack), 0);
    expect("field of the thread context", SiteFor(0x00E52E5F)->field, FieldFrameThreadContext);

    // Slots wrap at the capacity; a later free overwrites the slot, seq last.
    Entry e = {};
    e.block = 0x1111;
    Write(g_testRing, 1, e);
    expect("seq 1 in slot 0", g_testRing.entries[0].seq, 1);
    e.block = 0x2222;
    Write(g_testRing, kCapacity + 1, e);
    expect("seq capacity+1 in slot 0", g_testRing.entries[0].seq, kCapacity + 1);
    expect("its block", g_testRing.entries[0].block, 0x2222);
    expect("seq capacity in the last slot", Slot(kCapacity), kCapacity - 1);
    e.block = 0x3333;
    Write(g_testRing, kCapacity, e);
    expect("the last slot written", g_testRing.entries[kCapacity - 1].seq, kCapacity);
    expect("the last slot's block", g_testRing.entries[kCapacity - 1].block, 0x3333);
    expect("capacity", kCapacity, 131072);
    e.tid = 0x4444;
    Write(g_testRing, 2, e);
    expect("the thread", g_testRing.entries[1].tid, 0x4444);
    expect("layout", kLayoutVersion, 2);

    // The script thread: the first frame-release free names it; from then on a free on another thread is flagged.
    volatile long script = 0;
    expect("a chunk before the script thread is known", ThreadFlag(script, FieldFrameChunk, 300), 0);
    expect("a chunk claims nothing", static_cast<uint32_t>(script), 0);
    expect("the first frame release", ThreadFlag(script, FieldFrameArguments, 200), 0);
    expect("claims the script thread", static_cast<uint32_t>(script), 200);
    expect("a chunk on another thread", ThreadFlag(script, FieldFrameChunk, 300), kOffScriptThread);
    expect("a frame release on another thread", ThreadFlag(script, FieldFrameList, 300), kOffScriptThread);
    expect("claimed once", static_cast<uint32_t>(script), 200);
    expect("the frame's own free on the script thread", ThreadFlag(script, FieldFrame, 200), 0);
    uint32_t releases = 0;
    for (int field = 0; field <= FieldBoehm; ++field)
    {
        releases += IsFrameRelease(static_cast<uint8_t>(field)) ? 1u : 0u;
    }
    expect("frame-release fields (1-5, 7)", releases, 6);

    // The rate: frees over the last window of at least a second.
    Rate rate = {1000, 0, 0};
    Tick(rate, 50, 1500);
    expect("no full window yet", rate.perSec, 0);
    Tick(rate, 300, 3000);
    expect("300 frees in 2 s", rate.perSec, 150);
    Tick(rate, 301, 3100);
    expect("held until the next window", rate.perSec, 150);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("freering ok\n");
    return 0;
}

// The chunk reserve (chunk_reserve_logic.h): each row changes the one byte it means to, it is on unless
// SIMLINK_CHUNKRESERVE=0, and with the reserve a pool chunk's largest frame still leaves room before the descriptor.
int ChunkReserveLogic()
{
    using namespace simlink::chunkreserve;
    int bad = 0;
    auto expect = [&bad](const char* what, uint32_t got, uint32_t want) {
        if (got != want)
        {
            std::printf("chunkreserve: %s: %u, want %u\n", what, got, want);
            ++bad;
        }
    };

    // Each replacement differs from its original in one byte: the jcc's condition, or the lea's disp8.
    const uint32_t changedAt[] = {2, 3, 2};
    const uint32_t from[] = {0x7C, 0x8D, 0x0C};  // jl, jge (0f 8d), + 0xc
    const uint32_t to[] = {0x7E, 0x8F, 0x10};    // jle, jg (0f 8f), + 0x10
    expect("rows", static_cast<uint32_t>(kReserveCount), 3);
    for (size_t i = 0; i < kReserveCount && i < 3; ++i)
    {
        const ByteReplace& row = kReserveReplaces[i];
        uint32_t differ = 0;
        for (size_t b = 0; b < row.length; ++b)
        {
            differ += row.original[b] != row.replacement[b] ? 1u : 0u;
        }

        expect("bytes changed", differ, 1);
        expect("original byte", row.original[changedAt[i]], from[i]);
        expect("replacement byte", row.replacement[changedAt[i]], to[i]);
    }

    // On by default (native version 11): only SIMLINK_CHUNKRESERVE=0 turns it off. GetEnvironmentVariableW returns 0
    // for a variable that is not set or empty, and the size it needs for a value too long for the buffer.
    expect("not set: on, so the 3 rows are written", ReserveWanted(L"", 0) ? 1u : 0u, 1u);
    expect("=0: off", ReserveWanted(L"0", 1) ? 1u : 0u, 0u);
    expect("=1: on", ReserveWanted(L"1", 1) ? 1u : 0u, 1u);
    expect("=00: on", ReserveWanted(L"00", 2) ? 1u : 0u, 1u);
    expect("too long: on", ReserveWanted(L"", 20) ? 1u : 0u, 1u);

    // A pool chunk (0x7fc, data from +0xc) takes frames up to 0x7ec with the reserve: its room, 0x7f0, stays above
    // them, so no frame ends at +0x7fc.
    expect("pool room", kPoolRoom, 0x7F0);
    expect("the largest pool frame leaves room", kPoolRoom > kPoolFrameMax ? 1u : 0u, 1u);
    expect("its chunk size", kPoolFrameMax + 0x10, 0x7FC);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("chunkreserve ok\n");
    return 0;
}

// The on-screen walk (onscreen_logic.h): the row replaces the test's prologue with mov al, 1 / ret 4 (the
// function's own epilogue pops one argument), it is a whole instruction window, and it is on unless SIMLINK_ONSCREEN=0.
int OnScreenLogic()
{
    using namespace simlink::onscreen;
    int bad = 0;
    auto expect = [&bad](const char* what, uint32_t got, uint32_t want) {
        if (got != want)
        {
            std::printf("onscreen: %s: %u, want %u\n", what, got, want);
            ++bad;
        }
    };

    expect("rows", static_cast<uint32_t>(kOnScreenCount), 1);
    const ByteReplace& row = kOnScreenReplaces[0];
    expect("va", static_cast<uint32_t>(row.va), 0x00954240);
    expect("length", static_cast<uint32_t>(row.length), 6);

    // The original: push ebp (1) / mov ebp, esp (2) / and esp, imm8 (3): three whole instructions.
    expect("push ebp", row.original[0], 0x55);
    expect("mov ebp, esp", (static_cast<uint32_t>(row.original[1]) << 8) | row.original[2], 0x8BEC);
    expect("and esp, imm8", (static_cast<uint32_t>(row.original[3]) << 8) | row.original[4], 0x83E4);

    // The replacement: mov al, 1 / ret 4 / nop.
    expect("mov al, 1", (static_cast<uint32_t>(row.replacement[0]) << 8) | row.replacement[1], 0xB001);
    expect("ret", row.replacement[2], 0xC2);
    expect("ret pops 4", static_cast<uint32_t>(row.replacement[3]) | (static_cast<uint32_t>(row.replacement[4]) << 8), 4);
    expect("nop", row.replacement[5], 0x90);

    // On by default (native version 12): only SIMLINK_ONSCREEN=0 turns it off.
    expect("not set: on", OnScreenWanted(L"", 0) ? 1u : 0u, 1u);
    expect("=0: off", OnScreenWanted(L"0", 1) ? 1u : 0u, 0u);
    expect("=1: on", OnScreenWanted(L"1", 1) ? 1u : 0u, 1u);
    expect("too long: on", OnScreenWanted(L"", 20) ? 1u : 0u, 1u);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("onscreen ok\n");
    return 0;
}

// The GC heap lever (gc_heap_logic.h): the four windows and their immediates as read from the exe (40 MB and 8 MB,
// counted in 4 KB blocks), templates that differ from them only in the imm32, the rows each spec builds, the
// SIMLINK_GCHEAP parse, the timer's switch and the forced thunk's target.
int GcHeapLogic()
{
    using namespace simlink::gcheap;
    int bad = 0;
    auto expect = [&bad](const char* what, uint32_t got, uint32_t want) {
        if (got != want)
        {
            std::printf("gcheap: %s: %u, want %u\n", what, got, want);
            ++bad;
        }
    };

    expect("rows", static_cast<uint32_t>(kHeapCount), 4);
    const uint32_t vas[] = {0x00E4E168, 0x00E4A1D8, 0x00E4A1FF, 0x00E4A206};
    const uint32_t lengths[] = {5, 7, 7, 6};
    const uint32_t opcodes[] = {0xBD, 0x3D, 0x3D, 0xB8};  // mov ebp, imm32 / cmp eax, imm32 (twice) / mov eax, imm32
    const uint32_t stock[] = {0x2800, 0x800, 0x800, 0x800};
    const uint32_t templates[] = {0x16000, 0x2000, 0x2000, 0x2000};
    for (size_t i = 0; i < kHeapCount && i < 4; ++i)
    {
        const ByteReplace& row = kHeapReplaces[i];
        expect("va", static_cast<uint32_t>(row.va), vas[i]);
        expect("length", static_cast<uint32_t>(row.length), lengths[i]);
        expect("opcode", row.original[0], opcodes[i]);
        expect("template opcode", row.replacement[0], opcodes[i]);
        expect("stock immediate", ImmOf(row.original), stock[i]);
        expect("template immediate", ImmOf(row.replacement), templates[i]);
        expect("growth row", kRowIsGrowth[i] ? 1u : 0u, i == 0 ? 0u : 1u);
        uint32_t outside = 0;
        for (size_t b = 0; b < row.length; ++b)
        {
            const bool imm = b >= kImmOffset && b < kImmOffset + 4;
            outside += !imm && row.original[b] != row.replacement[b] ? 1u : 0u;
        }

        expect("bytes changed outside the immediate", outside, 0);
    }

    // The instruction after each comparison stays: jbe (76 2c), ja (77 05), push eax (50).
    expect("jbe", (static_cast<uint32_t>(kHeapGrowCmpLe[5]) << 8) | kHeapGrowCmpLe[6], 0x762C);
    expect("ja", (static_cast<uint32_t>(kHeapGrowCmpGt[5]) << 8) | kHeapGrowCmpGt[6], 0x7705);
    expect("push eax", kHeapGrowMov[5], 0x50);

    // Units: 4 KB blocks.
    expect("stock initial is 40 MB", kStockInitialBlocks, MBToBlocks(40));
    expect("stock growth is 8 MB", kStockGrowthBlocks, MBToBlocks(8));
    expect("352 MB", MBToBlocks(352), 0x16000);
    expect("128 MB", MBToBlocks(128), 0x8000);
    expect("32 MB", MBToBlocks(32), 0x2000);

    // The stock spec rebuilds every original, 352/32 every template, 128/32 its own immediates.
    const HeapSpec stockSpec = {40, 8};
    const HeapSpec theirs = {352, 32};
    const HeapSpec middle = {128, 32};
    for (size_t i = 0; i < kHeapCount; ++i)
    {
        const ByteReplace& row = kHeapReplaces[i];
        unsigned char built[kHeapRowMax] = {};
        BuildRow(row, RowBlocks(i, stockSpec), built);
        expect("stock spec rebuilds the original", std::memcmp(built, row.original, row.length) == 0 ? 1u : 0u, 1u);
        BuildRow(row, RowBlocks(i, theirs), built);
        expect("352/32 builds the template", std::memcmp(built, row.replacement, row.length) == 0 ? 1u : 0u, 1u);
        BuildRow(row, RowBlocks(i, middle), built);
        expect("128/32 immediate", ImmOf(built), i == 0 ? 0x8000u : 0x2000u);
        expect("128/32 keeps the opcode", built[0], row.original[0]);
    }

    // SIMLINK_GCHEAP's parse, against a 16-character buffer as gc_heap.cpp reads it.
    struct Case
    {
        const wchar_t* text;
        unsigned long length;
        SpecResult want;
        uint32_t initialMB;
        uint32_t growthMB;
    };
    const Case cases[] = {
        {L"", 0, SpecDefault, 352, 32},  // not set or empty: the default (native version 15)
        {L"0", 1, SpecOff, 0, 0},
        {L"00", 2, SpecMalformed, 0, 0},
        {L"0,32", 4, SpecOutOfRange, 0, 0},
        {L"352,32", 6, SpecOk, 352, 32},
        {L"128,32", 6, SpecOk, 128, 32},
        {L"40,8", 4, SpecOk, 40, 8},
        {L"512,128", 7, SpecOk, 512, 128},
        {L"0352,032", 8, SpecOk, 352, 32},
        {L"352", 3, SpecMalformed, 0, 0},
        {L"352,", 4, SpecMalformed, 0, 0},
        {L",32", 3, SpecMalformed, 0, 0},
        {L"352,32,1", 8, SpecMalformed, 0, 0},
        {L"352;32", 6, SpecMalformed, 0, 0},
        {L" 352,32", 7, SpecMalformed, 0, 0},
        {L"352,32 ", 7, SpecMalformed, 0, 0},
        {L"35200,32", 8, SpecMalformed, 0, 0},
        {L"-352,32", 7, SpecMalformed, 0, 0},
        {L"", 40, SpecMalformed, 0, 0},  // too long for the buffer: the size it needs
        {L"39,8", 4, SpecOutOfRange, 0, 0},
        {L"513,32", 6, SpecOutOfRange, 0, 0},
        {L"352,7", 5, SpecOutOfRange, 0, 0},
        {L"352,129", 7, SpecOutOfRange, 0, 0},
        {L"0,0", 3, SpecOutOfRange, 0, 0},
    };
    for (const Case& c : cases)
    {
        HeapSpec out = {99, 99};
        const SpecResult got = ParseSpec(c.text, c.length, 16, out);
        if (got != c.want || out.initialMB != c.initialMB || out.growthMB != c.growthMB)
        {
            std::printf("gcheap: parse \"%ls\" (%lu): %d %u,%u, want %d %u,%u\n", c.text, c.length, got, out.initialMB,
                        out.growthMB, c.want, c.initialMB, c.growthMB);
            ++bad;
        }
    }

    // The default is the A/B's heap352 and builds the templates.
    expect("default initial MB", kDefaultInitialMB, 352u);
    expect("default growth MB", kDefaultGrowthMB, 32u);

    // The forced timer: opt-in, SIMLINK_GCFORCED=1 only.
    expect("timer not set: off", ForcedTimerWanted(L"", 0) ? 1u : 0u, 0u);
    expect("timer =1: on", ForcedTimerWanted(L"1", 1) ? 1u : 0u, 1u);
    expect("timer =0: off", ForcedTimerWanted(L"0", 1) ? 1u : 0u, 0u);
    expect("timer =11: off", ForcedTimerWanted(L"11", 2) ? 1u : 0u, 0u);

    // The thunk at 0xD70800 jumps to the never-stop collect.
    expect("thunk opcode", kHeapForcedThunk[0], 0xE9);
    expect("thunk target", static_cast<uint32_t>(JumpTarget(0x00D70800, kHeapForcedThunk)),
           static_cast<uint32_t>(kNeverStopCollectAt));
    expect("never-stop collect", static_cast<uint32_t>(kNeverStopCollectAt), 0x00E4A320);
    expect("pause bands", kLongPauseUs < kVeryLongPauseUs ? 1u : 0u, 1u);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("gcheap ok\n");
    return 0;
}

// Walker counter copies (walker_copy_logic.h): where a copied count set goes, the pending table, the residual window, the
// re-resolve carry's pick and the handle table.
int WalkerCopyLogic()
{
    using namespace simlink::sacs;
    int bad = 0;
    auto expect = [&bad](const char* what, uint32_t got, uint32_t want) {
        if (got != want)
        {
            std::printf("walkers: %s: %u, want %u\n", what, got, want);
            ++bad;
        }
    };

    // Placement: only under the counts' own spare, on the walker when it exists.
    expect("place written", static_cast<uint32_t>(Place(true, 7, 7)), PlaceWritten);
    expect("place pending", static_cast<uint32_t>(Place(false, 7, 7)), PlacePending);
    expect("place spare differs", static_cast<uint32_t>(Place(true, 7, 8)), PlaceSpareDiffers);
    expect("place spare differs, no walker", static_cast<uint32_t>(Place(false, 0, 8)), PlaceSpareDiffers);

    // The residual window: open at the copied boundary, closed by the next boundary, closed once.
    WalkerEntry entry = {};
    const WalkerCounts copied = {4, 2, 9, 3};
    SetCounts(entry, copied);
    expect("set generation", entry.generation, 4);
    expect("set draws", entry.draws, 2);
    expect("set boundaries", entry.boundaries, 9);
    expect("set executions", entry.executions, 3);
    expect("window open", InCopiedWindow(entry) ? 1u : 0u, 1);
    expect("no close at the copied boundary", CloseWindow(entry) ? 1u : 0u, 0);
    entry.boundaries++;
    expect("window shut past it", InCopiedWindow(entry) ? 1u : 0u, 0);
    expect("closed by the next boundary", CloseWindow(entry) ? 1u : 0u, 1);
    expect("closed once", CloseWindow(entry) ? 1u : 0u, 0);
    WalkerEntry never = {};
    expect("never copied: no window", InCopiedWindow(never) ? 1u : 0u, 0);

    // Pending: taken only under its spare, by the walker's (re)start; a newer set replaces; full evicts the oldest.
    static PendingTable table;
    table = PendingTable();
    const uintptr_t driver = 0x1000;
    Stage(table, driver, 2, 0xAAAA, copied);
    WalkerEntry fresh = {};
    expect("other spare: from 0", StartCounts(fresh, 0x50, 0xBBBB, driver, 2, table) ? 1u : 0u, 0);
    expect("other spare: generation 0", fresh.generation, 0);
    expect("other spare: kept", table.live, 1);
    expect("other index: from 0", StartCounts(fresh, 0x50, 0xAAAA, driver, 1, table) ? 1u : 0u, 0);
    expect("its spare: taken", StartCounts(fresh, 0x50, 0xAAAA, driver, 2, table) ? 1u : 0u, 1);
    expect("taken generation", fresh.generation, 4);
    expect("taken boundaries", fresh.boundaries, 9);
    expect("taken: window open", InCopiedWindow(fresh) ? 1u : 0u, 1);
    expect("taken: entry knows its driver", fresh.driver == driver ? 1u : 0u, 1);
    expect("taken: entry knows its index", fresh.index, 2);
    expect("taken: freed", table.live, 0);
    expect("taken once", StartCounts(fresh, 0x50, 0xAAAA, driver, 2, table) ? 1u : 0u, 0);
    expect("taken count", table.taken, 1);

    const WalkerCounts newer = {5, 0, 10, 3};
    Stage(table, driver, 3, 0xAAAA, copied);
    Stage(table, driver, 3, 0xCCCC, newer);
    expect("same walker: replaced", table.replaced, 1);
    expect("same walker: one kept", table.live, 1);
    WalkerCounts got = {};
    expect("the older spare is gone", Take(table, driver, 3, 0xAAAA, got) ? 1u : 0u, 0);
    expect("the newer set is there", Take(table, driver, 3, 0xCCCC, got) ? 1u : 0u, 1);
    expect("newer generation", got.generation, 5);

    Stage(table, driver, 4, 0xAAAA, copied);
    Forget(table, driver, 4);
    expect("forgotten", table.live, 0);
    expect("forgotten: not taken", Take(table, driver, 4, 0xAAAA, got) ? 1u : 0u, 0);

    table = PendingTable();
    for (uint32_t i = 0; i < kPendingSlots; ++i)
    {
        Stage(table, 0x2000 + 8 * i, 0, 1, copied);
    }

    expect("full", table.live, kPendingSlots);
    Stage(table, 0x9000, 0, 1, newer);
    expect("evicted", table.evicted, 1);
    expect("still full", table.live, kPendingSlots);
    expect("the oldest went", Take(table, 0x2000, 0, 1, got) ? 1u : 0u, 0);
    expect("the second stayed", Take(table, 0x2008, 0, 1, got) ? 1u : 0u, 1);
    expect("the new one is in", Take(table, 0x9000, 0, 1, got) ? 1u : 0u, 1);

    // The carry: the old driver's entries under the spare, one per index, a replaced walker's later entry winning.
    WalkerEntry entries[6] = {};
    const uintptr_t oldDriver = 0x3000;
    entries[0] = {0x10, 0x55, 2, 1, 0, 3, oldDriver, 0, 0};
    entries[1] = {0x18, 0x55, 1, 0, 0, 5, oldDriver, 0, 0};  // index 0 again, further on: wins
    entries[2] = {0x20, 0x66, 9, 9, 9, 9, oldDriver, 1, 0};  // another spare
    entries[3] = {0x28, 0x55, 9, 9, 9, 9, 0x4000, 1, 0};     // another driver
    entries[4] = {0x30, 0x55, 0, 0, 0, 0, oldDriver, 2, 0};  // a walker that never moved still counts
    entries[5] = {0, 0x55, 7, 7, 7, 7, oldDriver, 3, 0};     // a free slot
    bool found[4];
    WalkerCounts counts[4];
    uintptr_t source[4];
    expect("carry indexes", CarryCollect(entries, 6, oldDriver, 0x55, found, counts, source, 4), 2);
    expect("carry index 0", found[0] ? 1u : 0u, 1);
    expect("carry index 0 boundaries", counts[0].boundaries, 5);
    expect("carry index 0 source", source[0] == 0x18 ? 1u : 0u, 1);
    expect("carry index 1 not found", found[1] ? 1u : 0u, 0);
    expect("carry index 2", found[2] ? 1u : 0u, 1);
    expect("carry index 3 (free slot) not found", found[3] ? 1u : 0u, 0);

    // The handle table: the last driver a handle resolved to; a crowded home keeps the newest.
    static HandleEntry handles[kHandleSlots];
    NoteHandle(handles, 0, 0x77);
    expect("handle 0 never noted", DriverOfHandle(handles, 0) == 0 ? 1u : 0u, 1);
    NoteHandle(handles, 41, 0x100);
    NoteHandle(handles, 41, 0x200);
    expect("handle: last driver", DriverOfHandle(handles, 41) == 0x200 ? 1u : 0u, 1);
    expect("unknown handle", DriverOfHandle(handles, 42) == 0 ? 1u : 0u, 1);
    for (uint32_t i = 1; i <= kHandleProbe; ++i)
    {
        NoteHandle(handles, 41 + kHandleSlots * i, 0x1000 + i);  // the same home slot as 41
    }

    expect("crowded: newest found", DriverOfHandle(handles, 41 + kHandleSlots * kHandleProbe) == 0x1000 + kHandleProbe ? 1u : 0u, 1);
    expect("crowded: earlier one found", DriverOfHandle(handles, 41 + kHandleSlots) == 0x1001 ? 1u : 0u, 1);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("walkers ok\n");
    return 0;
}

// Phase-0 pending-set fallback helper (sacs_pick_logic.h): first non-null bucket's entry[0], or 0 when empty/malformed.
int PickFallbackLogic()
{
    using namespace simlink::sacs;
    int bad = 0;
    auto expect = [&bad](const char* what, uintptr_t got, uintptr_t want) {
        if (got != want)
        {
            std::printf("pickfallback: %s: %zu, want %zu\n", what, static_cast<size_t>(got), static_cast<size_t>(want));
            ++bad;
        }
    };

    expect("null set", PickFallback(nullptr), 0);

    struct MockSet
    {
        uint32_t word0;
        const uintptr_t* buckets;
        uint32_t count;
        uint32_t size;
    };

    MockSet empty = {0, nullptr, 4, 0};
    expect("null buckets", PickFallback(reinterpret_cast<const unsigned char*>(&empty)), 0);

    const uintptr_t entry1[2] = {0x11110001u, 0};
    const uintptr_t entry2[2] = {0x22220002u, 0};
    uintptr_t buckets[4] = {0, 0, reinterpret_cast<uintptr_t>(entry1), reinterpret_cast<uintptr_t>(entry2)};

    MockSet zeroCount = {0, buckets, 0, 2};
    expect("zero bucket count", PickFallback(reinterpret_cast<const unsigned char*>(&zeroCount)), 0);

    MockSet overCap = {0, buckets, kMaxFallbackBuckets + 1, 2};
    expect("oversized bucket count", PickFallback(reinterpret_cast<const unsigned char*>(&overCap)), 0);

    uintptr_t allNull[3] = {0, 0, 0};
    MockSet noEntries = {0, allNull, 3, 0};
    expect("all null buckets", PickFallback(reinterpret_cast<const unsigned char*>(&noEntries)), 0);

    MockSet skipsEmpty = {0, buckets, 4, 2};
    expect("first non-null bucket wins", PickFallback(reinterpret_cast<const unsigned char*>(&skipsEmpty)), 0x11110001u);

    uintptr_t firstPopulated[2] = {reinterpret_cast<uintptr_t>(entry2), reinterpret_cast<uintptr_t>(entry1)};
    MockSet headBucket = {0, firstPopulated, 2, 2};
    expect("bucket 0 populated", PickFallback(reinterpret_cast<const unsigned char*>(&headBucket)), 0x22220002u);

    if (bad != 0)
    {
        return 1;
    }

    std::printf("pickfallback ok\n");
    return 0;
}

// 128-bit dual site digest (site_digest.h): known vectors, null handling, and single-byte mutation rejection.
int SiteDigestLogic()
{
    int bad = 0;
    auto checkVec = [&bad](const char* name, const void* data, size_t len, uint64_t wantHi, uint64_t wantLo) {
        const simlink::SiteDigest got = simlink::ComputeSiteDigest(data, len);
        if (got.hi != wantHi || got.lo != wantLo)
        {
            std::printf("sitedigest: %s: got {%016llX, %016llX}, want {%016llX, %016llX}\n", name, got.hi, got.lo, wantHi, wantLo);
            ++bad;
        }
    };

    const unsigned char zeroByte[1] = {0x00};
    const char sample[] = "SimLink-digest";
    checkVec("empty", "", 0, 0x6D8BA87CFCDB2A75ULL, 0xCBF29CE484222325ULL);
    checkVec("zero-byte", zeroByte, 1, 0x8624102343F1C24FULL, 0xAF64E14C8600C4E2ULL);
    checkVec("sample", sample, 14, 0x262DC197810E3804ULL, 0x6DE37C8B945307C9ULL);

    const simlink::SiteDigest expected = {0x262DC197810E3804ULL, 0x6DE37C8B945307C9ULL};
    if (!simlink::SiteDigestMatchesBytes(sample, 14, expected))
    {
        std::printf("sitedigest: bytes match failed\n");
        ++bad;
    }
    if (!simlink::SiteDigestMatches(reinterpret_cast<uintptr_t>(sample), 14, expected))
    {
        std::printf("sitedigest: va match failed\n");
        ++bad;
    }

    char mutated[] = "SimLink-digesu";
    if (simlink::SiteDigestMatchesBytes(mutated, 14, expected))
    {
        std::printf("sitedigest: mutated bytes matched unexpectedly\n");
        ++bad;
    }
    if (simlink::SiteDigestMatches(0, 14, expected) || simlink::SiteDigestMatchesBytes(nullptr, 14, expected))
    {
        std::printf("sitedigest: null address matched unexpectedly\n");
        ++bad;
    }

    if (bad != 0)
    {
        return 1;
    }

    std::printf("sitedigest ok\n");
    return 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 2 && std::wcscmp(argv[1], L"keyed") == 0)
    {
        return KeyedVectors();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"chunkreserve") == 0)
    {
        return ChunkReserveLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"onscreen") == 0)
    {
        return OnScreenLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"gcheap") == 0)
    {
        return GcHeapLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"walkers") == 0)
    {
        return WalkerCopyLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"pickfallback") == 0)
    {
        return PickFallbackLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"sitedigest") == 0)
    {
        return SiteDigestLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"freering") == 0)
    {
        return FreeRingLogic();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"gcgate") == 0)
    {
        return GcGateDecisions();
    }

    if (argc >= 2 && std::wcscmp(argv[1], L"claim-race") == 0)
    {
        return ClaimRace();
    }

    if (argc >= 6 && std::wcscmp(argv[1], L"consume-batches") == 0)
    {
        return BatchConsumer(argv[2], _wtoi(argv[3]), static_cast<uint32_t>(_wtoi(argv[4])), _wtoi(argv[5]));
    }

    if (argc >= 6 && (std::wcscmp(argv[1], L"consume") == 0 || std::wcscmp(argv[1], L"consume-chunked") == 0))
    {
        // consume-chunked: at most 7 records per Consume call, the rest resumed by the next (the early-stop path).
        const int limit = std::wcscmp(argv[1], L"consume-chunked") == 0 ? 7 : 0;
        return Consumer(argv[2], _wtoi(argv[3]), static_cast<uint32_t>(_wtoi(argv[4])), _wtoi(argv[5]), limit);
    }

    if (argc < 5)
    {
        std::fwprintf(stderr, L"usage: simlink_selftest <section-name> <count> <ring-bytes> <seconds>\n");
        return 2;
    }

    const wchar_t* name = argv[1];
    const int count = _wtoi(argv[2]);
    const uint32_t ringSize = static_cast<uint32_t>(_wtoi(argv[3]));
    const int seconds = _wtoi(argv[4]);
    if (ringSize < 4096 || (ringSize & (ringSize - 1)) != 0)
    {
        std::fwprintf(stderr, L"ring-bytes must be a power of two >= 4096\n");
        return 2;
    }

    HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, simlink::kHeaderSize + ringSize, name);
    if (section == nullptr)
    {
        std::fwprintf(stderr, L"CreateFileMapping failed: %lu\n", GetLastError());
        return 1;
    }

    unsigned char* base = static_cast<unsigned char*>(MapViewOfFile(section, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    simlink::Ring ring;
    simlink::InitSection(ring, base, ringSize, GetCurrentProcessId(), "SimLink selftest");
    simlink::Store32(base, simlink::off::StringLengthOffset, 4);
    std::printf("ready\n");
    std::fflush(stdout);

    long long retries = 0;
    for (int i = 1; i <= count; ++i)
    {
        std::wstring payload = std::to_wstring(i) + L"|T|" + std::wstring(static_cast<size_t>(i % 97), L'x');
        LARGE_INTEGER now;
        for (;;)
        {
            QueryPerformanceCounter(&now);
            if (simlink::Publish(ring, 1, i, payload.c_str(), static_cast<uint32_t>(payload.size()), now.QuadPart))
            {
                break;
            }

            ++retries;
            Sleep(0);
        }
    }

    simlink::Store32(base, simlink::off::NativeVersion, 0xFFFFFFFFu);
    std::printf("published=%d retries=%lld wraps>=%llu\n", count, retries,
                simlink::Load64(base, simlink::off::WritePos) / ringSize);
    std::fflush(stdout);
    Sleep(static_cast<DWORD>(seconds) * 1000u);
    return 0;
}
