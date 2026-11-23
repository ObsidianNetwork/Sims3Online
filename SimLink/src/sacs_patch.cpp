// tier 3: keyed path picks. Addresses and site digests correspond to Steam 1.67.2.024037 TS3W.exe (image base
// 0x400000, no ASLR); every site is digest-checked before anything is written.
//
// The path finder draws among candidate paths with a throwaway LCG seeded from rdtsc (0x4FA840). Two query sites
// matter: the walker's own step (the loop variant, 0x803D7A) and a request's first hop (0x80314C). SimLink wraps
// both, so that the two draw sites inside the query (0x7FCB59 mode 2, 0x7FCC38 mode 0) know which walker asked; and
// it wraps the two calls that hand a request to a walker (0x80443D, 0x804482), which count the walker's request
// generation. With switch bit 0 on, a draw made inside a wrapped query is replaced by
//     mix(driver spare seed, walker index, site, request generation, draws in that generation)
// scaled to the candidate count. The spare seed (driver +0x34, no SACS code draws from it) is copied server ->
// client by tier 1, so both games compute the same pick without exchanging it. Candidates, their order and the
// Deterministic branch stay the game's own. Draws made elsewhere (0x803CBC, whose pick is discarded) stay native.
//
// Item 3 (the keyed Random-node draw, switch bit 3) is further down.
#include "sacs_patch.h"

#include <cstring>

#include "sacs_pick_logic.h"
#include "sacs_random.h"
#include "site_digest.h"
#include "walker_copy_logic.h"

namespace simlink
{
bool RegistrarBytesMatch();  // sidecar.cpp: pins the build
int ResolveSeedDriver(uint32_t handle, unsigned char** driver);  // sidecar.cpp: a script driver handle, as the seed calls

namespace sacs
{
namespace
{
constexpr uint32_t kDriverVtable = 0x01020110;
constexpr size_t kDriverSpareSeed = 0x34;
constexpr size_t kDriverWalkersBegin = 0x10;  // 8-byte entries, the walker pointer at +4
constexpr size_t kDriverWalkersEnd = 0x14;
constexpr size_t kDriverContext = 0x44;  // points back at the driver
constexpr size_t kWalkerDriver = 0x04;
constexpr size_t kNodeHash = 0x28;

enum Site : uint32_t
{
    SiteWalk = 1,     // 0x803D7A: the walker's next state (a loop hub's variant)
    SiteRequest = 2,  // 0x80314C: the first state of a requested route
};

enum Counter : int
{
    CounterQueries = 0,
    CounterInvalidWalker = 1,
    CounterDraws = 2,
    CounterKeyed = 3,
    CounterRequests = 4,
    CounterTableFull = 5,
    CounterDrawsOutside = 6,
    CounterCount = 7,
};

volatile LONG g_switches = 0;
volatile LONG g_state = PatchNotAttempted;
volatile LONG g_detail = 0;
volatile LONG g_counters[CounterCount];

// Item 3's diagnostics (RandomCounter).
enum RandomCounterId : int
{
    RandomEvaluations = 0,  // Random-node evaluates, whatever the switch
    RandomDrawsIn = 1,      // draws inside a noted evaluate
    RandomKeyed = 2,        // draws replaced by the keyed value (switch bit 3)
    RandomRetries = 3,      // draws after the first in one evaluate (the no-repeat retry), keyed or not
    RandomResets = 4,       // keyed evaluations that set the node's history empty (a new boundary, or a new spare)
    RandomNoWalker = 5,     // fallback: the walker argument is not a walker of a driver
    RandomNoContext = 6,    // fallback: the context argument is not the walker's driver + 0x44
    RandomNoKey = 7,        // fallback: the node has no site A' position (its graph's load was not read)
    RandomNoSpare = 8,      // fallback: the driver's spare seed is 0
    RandomFull = 9,         // fallback: no free slot in the walker or evaluation table
    RandomException = 10,   // fallback: an access violation setting the history empty
    RandomOutside = 11,     // draws at 0x7F8951 outside a noted evaluate (the game's own value)
    RandomEnters = 12,      // EnterState boundaries counted
    RandomLogged = 13,      // input log records written (switch bit 4)
    RandomCounterCount = 14,
};
volatile LONG g_randomCounters[RandomCounterCount];

// Option C's diagnostics (WalkerCounter; walker_copy_logic.h). The pending table keeps its own four counts.
enum WalkerCounterId : int
{
    WalkerReads = 0,          // server: walkers read (WalkerRead)
    WalkerWritten = 1,        // copy: count sets put on their walker at once
    WalkerStaged = 2,         // copy: kept until the walker exists
    WalkerSpareDiffers = 3,   // copy: kept until the driver carries the counts' spare
    WalkerNoDriver = 4,       // the handle did not resolve to a driver (read, write or carry)
    WalkerPendingStaged = 5,  // the pending table's counts (5-8)
    WalkerPendingReplaced = 6,
    WalkerPendingTaken = 7,
    WalkerPendingEvicted = 8,
    WalkerCarries = 9,        // copy: re-resolve carries that found the old driver
    WalkerCarried = 10,       // copy: walkers carried (written or kept)
    WalkerCarryNoOld = 11,    // copy: the old handle was never resolved here
    WalkerWindows = 12,       // residual windows opened (a count set landed on its walker)
    WalkerWindowsClosed = 13, // ... and closed by the walker's next request or EnterState
    WalkerResidualEvals = 14, // Random evaluates (keyed or logged) inside an open window (per-node state not copied)
    WalkerResidualDraws = 15, // tier 3 draws inside an open window
    WalkerExceptions = 16,
    WalkerTableFull = 17,     // a write found no walker entry free
    WalkerCounterCount = 18,
};
volatile LONG g_walkerCounters[WalkerCounterCount];

// ---- per-walker request generations ----

constexpr uint32_t kWalkerSlots = 16384;  // a power of two; entries are never removed (reset when the spare changes)
WalkerEntry g_walkers[kWalkerSlots];
SRWLOCK g_walkerLock = SRWLOCK_INIT;
PendingTable g_pending;  // under g_walkerLock

struct WalkerFacts
{
    uint32_t spare;
    uint32_t index;  // the walker's position in its driver's walker list (the same on both games)
    uintptr_t driver;
};

// (Re)starts an entry's counters: from 0, or from a count set option C kept for this walker under this spare.
void StartEntry(WalkerEntry& entry, uintptr_t walker, const WalkerFacts& facts)
{
    if (StartCounts(entry, walker, facts.spare, facts.driver, facts.index, g_pending))
    {
        InterlockedIncrement(&g_walkerCounters[WalkerWindows]);
    }
}

// Returns the entry for walker (created on first use, reset when its driver's spare seed changed: a reused pointer).
// Call with the lock held. nullptr when the table is full.
WalkerEntry* FindWalker(uintptr_t walker, const WalkerFacts& facts)
{
    uint32_t slot = static_cast<uint32_t>((walker >> 3) * 2654435761u) & (kWalkerSlots - 1u);
    for (uint32_t probe = 0; probe < kWalkerSlots; ++probe, slot = (slot + 1u) & (kWalkerSlots - 1u))
    {
        WalkerEntry& entry = g_walkers[slot];
        if (entry.walker == walker)
        {
            if (entry.spare != facts.spare)
            {
                StartEntry(entry, walker, facts);
            }

            return &entry;
        }

        if (entry.walker == 0)
        {
            StartEntry(entry, walker, facts);
            return &entry;
        }
    }

    InterlockedIncrement(&g_counters[CounterTableFull]);
    return nullptr;
}

// The entry for walker as it stands, without creating or resetting it (call with the lock held, shared is enough).
const WalkerEntry* LookWalker(uintptr_t walker)
{
    uint32_t slot = static_cast<uint32_t>((walker >> 3) * 2654435761u) & (kWalkerSlots - 1u);
    for (uint32_t probe = 0; probe < kWalkerSlots; ++probe, slot = (slot + 1u) & (kWalkerSlots - 1u))
    {
        const WalkerEntry& entry = g_walkers[slot];
        if (entry.walker == walker)
        {
            return &entry;
        }

        if (entry.walker == 0)
        {
            return nullptr;
        }
    }

    return nullptr;
}

// A boundary moved (a request or an EnterState): closes the entry's residual window if a copy left one open.
void NoteBoundary(WalkerEntry& entry)
{
    if (CloseWindow(entry))
    {
        InterlockedIncrement(&g_walkerCounters[WalkerWindowsClosed]);
    }
}

// Reads a walker's driver, spare seed and index. False when the memory does not look like a walker of a driver.
bool ReadWalker(uintptr_t walker, WalkerFacts& facts)
{
    __try
    {
        if (walker == 0)
        {
            return false;
        }

        const unsigned char* driver = *reinterpret_cast<unsigned char* const*>(walker + kWalkerDriver);
        if (driver == nullptr || *reinterpret_cast<const uint32_t*>(driver) != kDriverVtable ||
            *reinterpret_cast<const unsigned char* const*>(driver + kDriverContext) != driver)
        {
            return false;
        }

        facts.driver = reinterpret_cast<uintptr_t>(driver);
        facts.spare = *reinterpret_cast<const volatile uint32_t*>(driver + kDriverSpareSeed);
        const unsigned char* begin = *reinterpret_cast<unsigned char* const*>(driver + kDriverWalkersBegin);
        const unsigned char* end = *reinterpret_cast<unsigned char* const*>(driver + kDriverWalkersEnd);
        if (begin == nullptr || end < begin || end - begin > 8 * 64)
        {
            return false;
        }

        for (uint32_t i = 0; begin + 8 * i < end; ++i)
        {
            if (*reinterpret_cast<const uintptr_t*>(begin + 8 * i + 4) == walker)
            {
                facts.index = i;
                return true;
            }
        }

        return false;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

uint32_t ReadHash(uintptr_t node)
{
    __try
    {
        return node != 0 ? *reinterpret_cast<const uint32_t*>(node + kNodeHash) : 0u;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0u;
    }
}

// ---- the pick log ----

volatile LONG g_pickCount = 0;
uint32_t g_picks[kPickCapacity][kPickWords];

// ---- the query in progress on this thread ----

struct Query
{
    int valid;
    int drew;
    uint32_t site;
    uintptr_t walker;
    uint32_t spare;
    uint32_t index;
    uintptr_t driver;
    uint32_t generation;
    uint32_t draws;
    uint32_t from;
    uint32_t to;
    uint32_t n;
    uint32_t chosen;
    uint32_t native;
    uint32_t drawMode;
    uint32_t keyed;
};
thread_local Query t_query;

uint64_t Mix64(uint64_t x)
{
    x ^= x >> 30;
    x *= 0xBF58476D1CE4E5B9ull;
    x ^= x >> 27;
    x *= 0x94D049BB133111EBull;
    x ^= x >> 31;
    return x;
}

uint32_t KeyedDraw(const Query& q, uint32_t n)
{
    const uint64_t a = (static_cast<uint64_t>(q.spare) << 32) | (q.index << 8) | q.site;
    const uint64_t b = (static_cast<uint64_t>(q.generation) << 32) | q.draws;
    const uint64_t r = Mix64(a ^ Mix64(b));
    return static_cast<uint32_t>((static_cast<uint64_t>(static_cast<uint32_t>(r >> 32)) * n) >> 32);
}

void __cdecl NoteRequest(uintptr_t walker)
{
    InterlockedIncrement(&g_counters[CounterRequests]);
    WalkerFacts facts;
    if (!ReadWalker(walker, facts))
    {
        InterlockedIncrement(&g_counters[CounterInvalidWalker]);
        return;
    }

    AcquireSRWLockExclusive(&g_walkerLock);
    WalkerEntry* entry = FindWalker(walker, facts);
    if (entry != nullptr)
    {
        entry->generation++;
        entry->draws = 0;
        entry->boundaries++;
        NoteBoundary(*entry);
    }

    ReleaseSRWLockExclusive(&g_walkerLock);
}

void __cdecl EnterQuery(uint32_t site, uintptr_t walker, uintptr_t start, uintptr_t destination)
{
    Query& q = t_query;
    q.valid = 0;
    q.drew = 0;
    InterlockedIncrement(&g_counters[CounterQueries]);
    WalkerFacts facts;
    if (!ReadWalker(walker, facts))
    {
        InterlockedIncrement(&g_counters[CounterInvalidWalker]);
        return;
    }

    AcquireSRWLockExclusive(&g_walkerLock);
    WalkerEntry* entry = FindWalker(walker, facts);
    const uint32_t generation = entry != nullptr ? entry->generation : 0u;
    ReleaseSRWLockExclusive(&g_walkerLock);
    if (entry == nullptr)
    {
        return;
    }

    q.site = site;
    q.walker = walker;
    q.spare = facts.spare;
    q.index = facts.index;
    q.driver = facts.driver;
    q.generation = generation;
    q.from = ReadHash(start);
    q.to = ReadHash(destination);
    q.valid = 1;
}

void __cdecl LeaveQuery(uintptr_t result)
{
    Query& q = t_query;
    if (q.valid && q.drew)
    {
        const uint32_t seq = static_cast<uint32_t>(InterlockedIncrement(&g_pickCount)) - 1u;
        uint32_t* record = g_picks[seq & (kPickCapacity - 1u)];
        record[kPickWords - 1] = 0;
        MemoryBarrier();
        record[0] = q.spare;
        record[1] = (q.index & 0xFFu) | ((q.site & 0xFFu) << 8) | ((q.drawMode & 0xFFu) << 16) | ((q.keyed & 0xFFu) << 24);
        record[2] = q.generation;
        record[3] = q.draws;
        record[4] = (q.n & 0xFFFFu) | (q.chosen << 16);
        record[5] = q.native;
        record[6] = q.from;
        record[7] = q.to;
        record[8] = ReadHash(result);
        MemoryBarrier();
        record[kPickWords - 1] = seq + 1u;
    }

    q.valid = 0;
    q.drew = 0;
}

typedef uint32_t(__fastcall* DrawFn)(uint32_t* lcg, void* unusedEdx, uint32_t n);  // thiscall, callee pops n
const DrawFn kOriginalDraw = reinterpret_cast<DrawFn>(0x004FA840);

uint32_t Draw(uint32_t* lcg, uint32_t n, uint32_t mode)
{
    const uint32_t native = kOriginalDraw(lcg, nullptr, n);  // the game's own draw (advances its throwaway LCG)
    Query& q = t_query;
    if (!q.valid || q.drew)
    {
        InterlockedIncrement(&g_counters[CounterDrawsOutside]);
        return native;
    }

    InterlockedIncrement(&g_counters[CounterDraws]);
    const WalkerFacts facts = {q.spare, q.index, q.driver};
    AcquireSRWLockExclusive(&g_walkerLock);
    WalkerEntry* entry = FindWalker(q.walker, facts);
    q.draws = entry != nullptr ? ++entry->draws : 0u;
    const bool residual = entry != nullptr && InCopiedWindow(*entry);
    ReleaseSRWLockExclusive(&g_walkerLock);
    if (residual)
    {
        InterlockedIncrement(&g_walkerCounters[WalkerResidualDraws]);
    }
    const uint32_t keyed = n > 1 ? KeyedDraw(q, n) : 0u;
    const bool on = (g_switches & kSwitchKeyedPicks) != 0 && entry != nullptr && q.spare != 0;
    q.drew = 1;
    q.n = n;
    q.native = native;
    q.chosen = on ? keyed : native;
    q.drawMode = mode;
    q.keyed = on ? 1u : 0u;
    if (on)
    {
        InterlockedIncrement(&g_counters[CounterKeyed]);
    }

    return q.chosen;
}

uint32_t __fastcall DrawAllPaths(uint32_t* lcg, void*, uint32_t n)
{
    return Draw(lcg, n, 2);
}

uint32_t __fastcall DrawShortestPaths(uint32_t* lcg, void*, uint32_t n)
{
    return Draw(lcg, n, 0);
}

// ---- thunks (entered by the retargeted `call`, so [esp] is the return address into the game) ----

// 0x803D7A: walker step, ESI = walker. 0x7FCE00 is thiscall (ECX = the stack path finder) with six stack arguments
// (start node, destination node, flags, traversal level, two exclusion words) and `ret 0x18`.
__declspec(naked) void QueryWalkThunk()
{
    __asm {
        push ecx
        push dword ptr [esp + 0Ch]  // destination
        push dword ptr [esp + 0Ch]  // start
        push esi                    // walker
        push 1                      // SiteWalk
        call EnterQuery
        add esp, 10h
        pop ecx
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        mov eax, 007FCE00h
        call eax
        push eax
        push eax
        call LeaveQuery
        add esp, 4
        pop eax
        ret 18h
    }
}

// 0x80314C: request planning, EDI = walker (EBP holds flags & 1 there, not the driver).
__declspec(naked) void QueryRequestThunk()
{
    __asm {
        push ecx
        push dword ptr [esp + 0Ch]
        push dword ptr [esp + 0Ch]
        push edi
        push 2  // SiteRequest
        call EnterQuery
        add esp, 10h
        pop ecx
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        push dword ptr [esp + 18h]
        mov eax, 007FCE00h
        call eax
        push eax
        push eax
        call LeaveQuery
        add esp, 4
        pop eax
        ret 18h
    }
}

// 0x80443D / 0x804482: a request handed to a walker. 0x802E90 is thiscall (ECX = driver) with four stack arguments
// (walker, destination node, flags, a fourth word); count the generation, then continue into it unchanged.
__declspec(naked) void RequestThunk()
{
    __asm {
        push ecx
        push dword ptr [esp + 8]  // walker
        call NoteRequest
        add esp, 4
        pop ecx
        mov eax, 00802E90h
        jmp eax
    }
}

// Item 3: a walker entering a state is a boundary too (a late join's or a dropped request's E| line on a client, or a
// script's EnterState). Tier 3's generation does not count it.
void __cdecl NoteEnter(uintptr_t walker, uintptr_t state)
{
    WalkerFacts facts;
    if (state == 0 || !ReadWalker(walker, facts))
    {
        return;  // 0x802E00 does nothing without a walker and a state
    }

    AcquireSRWLockExclusive(&g_walkerLock);
    WalkerEntry* entry = FindWalker(walker, facts);
    if (entry != nullptr)
    {
        entry->boundaries++;
        NoteBoundary(*entry);
    }

    ReleaseSRWLockExclusive(&g_walkerLock);
    InterlockedIncrement(&g_randomCounters[RandomEnters]);
}

// 0x80425C / 0x804277 (Driver::EnterState, 0x804210, its only caller): 0x802E00 is thiscall (ECX = driver) with two
// stack arguments (walker, state) and reads neither EAX nor EDX before writing them; count the boundary, then continue
// into it unchanged.
__declspec(naked) void EnterThunk()
{
    __asm {
        push ecx
        push dword ptr [esp + 0Ch]  // state
        push dword ptr [esp + 0Ch]  // walker
        call NoteEnter
        add esp, 8
        pop ecx
        mov eax, 00802E00h
        jmp eax
    }
}

// ---- site A': the decision graph's pick order ----
//
// The executor (0x7F5400, this = the decision graph) runs a pending set of nodes; phase 0 takes the head of the first
// non-empty bucket (0x7F55C3-0x7F55E3), and the bucket is the node's heap address modulo the bucket count, so the order
// differs between processes. A' records, when a graph loads, each node's position in the graph's list 1 (file order),
// and with switch bit 1 on picks the pending node with the smallest position instead. Both games load the same
// JazzData, so both run a state's nodes in the same order.

struct GraphNode
{
    uintptr_t node;
    uintptr_t graph;
    uint32_t key;      // position in the graph's list 1, plus 1
    uint32_t graphId;  // a hash of the graph's chunk (references and node vtables): the same in every process
};
constexpr uint32_t kNodeSlots = 1u << 18;  // a power of two; entries are overwritten when a node pointer is reused
constexpr uint32_t kNodeProbe = 64;
constexpr uint32_t kMaxGraphNodes = 512;  // the largest graph in the game has 92 nodes
constexpr uint32_t kMaxPending = 512;
constexpr uint32_t kMaxBuckets = 1u << 16;
constexpr size_t kNodeGraph = 0x28;  // a decision-graph node's graph (0x7F41A0 sets it)
GraphNode g_nodes[kNodeSlots];
SRWLOCK g_nodeLock = SRWLOCK_INIT;  // the loader may run on another thread than the executor

enum OrderCounterId : int
{
    OrderPicks = 0,        // phase-0 picks scanned (switch bit 1 or 2 on)
    OrderSingle = 1,       // ... with one pending node (nothing to order)
    OrderApplied = 2,      // file-order pick used (bit 1)
    OrderDiffers = 3,      // sets of two or more where the file-order pick is not the game's own
    OrderNoKey = 4,        // fallback: a pending node has no recorded position
    OrderStale = 5,        // fallback: a node's graph is not the one recorded with its position
    OrderMixed = 6,        // fallback: nodes of two graphs pending together
    OrderDuplicate = 7,    // fallback: two pending nodes share a position
    OrderBadSet = 8,       // fallback: the set's buckets and size do not add up
    OrderException = 9,    // fallback: an access violation while scanning
    OrderGraphs = 10,      // graphs recorded at load
    OrderNodes = 11,       // nodes recorded at load
    OrderLoadFailed = 12,  // loads whose chunk did not read as expected (their nodes have no position)
    OrderTableFull = 13,   // nodes not recorded: no free slot within the probe
    OrderLogged = 14,      // order records written (executions)
    OrderNoWalker = 15,    // steps whose walker did not read as a walker of a driver (not logged)
    OrderMidExecution = 16,  // steps of an execution that began before the log was on (not logged)
    OrderCounterCount = 17,
};
volatile LONG g_orderCounters[OrderCounterCount];

uint32_t Fnv(uint32_t hash, uint32_t word)
{
    for (int i = 0; i < 4; ++i)
    {
        hash ^= (word >> (8 * i)) & 0xFFu;
        hash *= 16777619u;
    }

    return hash;
}

uint32_t NodeSlot(uintptr_t node)
{
    return static_cast<uint32_t>((node >> 3) * 2654435761u) & (kNodeSlots - 1u);
}

// Call with g_nodeLock held exclusively. Nothing is ever removed, so an empty slot ends a probe.
void PutNode(uintptr_t node, uintptr_t graph, uint32_t key, uint32_t graphId)
{
    uint32_t slot = NodeSlot(node);
    for (uint32_t probe = 0; probe < kNodeProbe; ++probe, slot = (slot + 1u) & (kNodeSlots - 1u))
    {
        GraphNode& entry = g_nodes[slot];
        if (entry.node == node || entry.node == 0)
        {
            entry.node = node;
            entry.graph = graph;
            entry.key = key;
            entry.graphId = graphId;
            return;
        }
    }

    InterlockedIncrement(&g_orderCounters[OrderTableFull]);
}

// Call with g_nodeLock held (either side).
const GraphNode* FindNode(uintptr_t node)
{
    uint32_t slot = NodeSlot(node);
    for (uint32_t probe = 0; probe < kNodeProbe; ++probe, slot = (slot + 1u) & (kNodeSlots - 1u))
    {
        const GraphNode& entry = g_nodes[slot];
        if (entry.node == node)
        {
            return &entry;
        }

        if (entry.node == 0)
        {
            return nullptr;
        }
    }

    return nullptr;
}

// Reads a loaded DecisionGraph chunk's list 1 into nodes (file order). After the game's own load (0x7FDF40 ->
// 0x7F44F0) the chunk's words are in native order: [1] the version (0x100-0x1FF), [3] the list-1 count, the list-1
// references, the list-2 (root) count, the root references. A reference resolves through the RCOL resource's tables:
// kind = the top nibble (< 4), index = the low 28 bits - 1, the table at resource + kind * 12 + 0x28 and its count at
// +0x2C (0x726690). A slot with its low bit set is not loaded; 0x726690 would load it, this never does. Returns the
// node count, or 0 when anything does not read as expected.
uint32_t ReadGraph(uintptr_t graph, uintptr_t stream, uintptr_t resource, uintptr_t* nodes, uint32_t& graphId)
{
    __try
    {
        const uintptr_t inner = *reinterpret_cast<const uintptr_t*>(stream + 4);
        if (inner == 0 || resource == 0)
        {
            return 0;
        }

        const uint32_t* data = *reinterpret_cast<const uint32_t* const*>(inner + 8);
        if (data == nullptr || data[1] < 0x100u || data[1] > 0x1FFu)
        {
            return 0;
        }

        const uint32_t count = data[3];
        const uint32_t* refs = data + 4;
        if (count == 0 || count > kMaxGraphNodes || refs[count] > kMaxGraphNodes)
        {
            return 0;
        }

        uint32_t hash = Fnv(2166136261u, count);
        for (uint32_t i = 0; i < count; ++i)
        {
            const uint32_t ref = refs[i];
            const uint32_t kind = ref >> 28;
            const uint32_t index = (ref & 0x0FFFFFFFu) - 1u;
            const unsigned char* table = reinterpret_cast<const unsigned char*>(resource) + kind * 12u;
            if (kind >= 4u || (ref & 0x0FFFFFFFu) == 0 || index >= *reinterpret_cast<const uint32_t*>(table + 0x2C))
            {
                return 0;
            }

            const uintptr_t node = (*reinterpret_cast<const uintptr_t* const*>(table + 0x28))[index];
            if (node == 0 || (node & 1u) != 0 || *reinterpret_cast<const uintptr_t*>(node + kNodeGraph) != graph)
            {
                return 0;
            }

            nodes[i] = node;
            hash = Fnv(Fnv(hash, ref), *reinterpret_cast<const uint32_t*>(node));  // the reference and the node's vtable
        }

        const uint32_t roots = refs[count];
        hash = Fnv(hash, roots);
        for (uint32_t i = 0; i < roots; ++i)
        {
            hash = Fnv(hash, refs[count + 1u + i]);
        }

        graphId = hash;
        return count;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}

void NoteGraph(uintptr_t graph, uintptr_t stream, uintptr_t resource)
{
    uintptr_t nodes[kMaxGraphNodes];
    uint32_t graphId = 0;
    const uint32_t count = ReadGraph(graph, stream, resource, nodes, graphId);
    if (count == 0)
    {
        InterlockedIncrement(&g_orderCounters[OrderLoadFailed]);
        return;
    }

    AcquireSRWLockExclusive(&g_nodeLock);
    for (uint32_t i = 0; i < count; ++i)
    {
        PutNode(nodes[i], graph, i + 1u, graphId);
    }

    ReleaseSRWLockExclusive(&g_nodeLock);
    InterlockedIncrement(&g_orderCounters[OrderGraphs]);
    InterlockedExchangeAdd(&g_orderCounters[OrderNodes], static_cast<LONG>(count));
}

// 0x101F528 (the DecisionGraph factory's vtable slot +0x38) points here: thiscall, ECX = the factory (unused), four
// stack arguments (the DecisionGraph, the chunk stream, the RCOL resource, a byte-swap flag), callee pops 16, success
// in AL. As __fastcall the unused EDX is the second parameter and the stack arguments follow.
typedef bool(__fastcall* GraphLoadFn)(void* factory, void* edx, uintptr_t graph, uintptr_t stream, uintptr_t resource,
                                      uint32_t swap);
const GraphLoadFn kOriginalGraphLoad = reinterpret_cast<GraphLoadFn>(0x007FDF40);

bool __fastcall GraphLoad(void* factory, void* edx, uintptr_t graph, uintptr_t stream, uintptr_t resource, uint32_t swap)
{
    const bool loaded = kOriginalGraphLoad(factory, edx, graph, stream, resource, swap);
    if (loaded)
    {
        NoteGraph(graph, stream, resource);
    }

    return loaded;
}

// ---- the order log ----

volatile LONG g_orderCount = 0;
uint32_t g_orders[kOrderCapacity][kOrderWords];

struct Scan
{
    uintptr_t graph;
    uint32_t graphId;
    uint32_t pending;
    uintptr_t best;
    uint32_t bestKey;
    uint32_t nativeKey;  // the key of the node the game's own block takes (the head of the first non-empty bucket)
    uint32_t mask[4];    // keys 1..128 pending
    uint32_t overflow;   // a key above 128
};

// Call with g_nodeLock held (shared). The pending set: +0x04 bucket array, +0x08 bucket count, +0x0C size; entries are
// {node, next}. Returns a fallback OrderCounterId, or -1 when every pending node has a position in one graph.
int ScanSet(const unsigned char* set, Scan& s)
{
    __try
    {
        const uintptr_t* buckets = *reinterpret_cast<const uintptr_t* const*>(set + 4);
        const uint32_t count = *reinterpret_cast<const uint32_t*>(set + 8);
        const uint32_t size = *reinterpret_cast<const uint32_t*>(set + 0xC);
        if (buckets == nullptr || count == 0 || count > kMaxBuckets || size == 0 || size > kMaxPending)
        {
            return OrderBadSet;
        }

        for (uint32_t b = 0; b < count && s.pending < size; ++b)
        {
            for (const uintptr_t* entry = reinterpret_cast<const uintptr_t*>(buckets[b]); entry != nullptr;
                 entry = reinterpret_cast<const uintptr_t*>(entry[1]))
            {
                if (++s.pending > size)
                {
                    return OrderBadSet;
                }

                const uintptr_t node = entry[0];
                const GraphNode* known = FindNode(node);
                if (known == nullptr)
                {
                    return OrderNoKey;
                }

                if (*reinterpret_cast<const uintptr_t*>(node + kNodeGraph) != known->graph)
                {
                    return OrderStale;
                }

                if (s.graph == 0)
                {
                    s.graph = known->graph;
                    s.graphId = known->graphId;
                    s.nativeKey = known->key;
                }
                else if (known->graph != s.graph)
                {
                    return OrderMixed;
                }

                const uint32_t key = known->key;
                if (key >= 1u && key <= 128u)
                {
                    const uint32_t bit = 1u << ((key - 1u) & 31u);
                    if ((s.mask[(key - 1u) >> 5] & bit) != 0)
                    {
                        return OrderDuplicate;
                    }

                    s.mask[(key - 1u) >> 5] |= bit;
                }
                else
                {
                    s.overflow = 1;
                }

                if (key == s.bestKey)
                {
                    return OrderDuplicate;
                }

                if (key < s.bestKey)
                {
                    s.bestKey = key;
                    s.best = node;
                }
            }
        }

        return s.pending == size ? -1 : OrderBadSet;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return OrderException;
    }
}

// One record per phase-0 execution, as the design asks: the driver key (the walker's driver's spare seed, copied by
// tier 1, and the walker's index on the driver), the walker's execution ordinal, the graph, and the positions of the
// first kExecKeys nodes in the order they ran. The executor's callers pass the walker as its third argument
// ([ebp+0x10]; walker +0x78 the state, +0x7C the queue phase 0 appends each picked node to, after clearing it), so an
// empty queue at a pick marks an execution's first pick. A record is written when the walker's next execution starts
// (or its slot is reused), so the newest execution of each walker is still open when the log is read.
constexpr size_t kWalkerQueue = 0x7C;  // a deque: +0x08 first element, +0x18 end
constexpr int kExecKeys = 16;
constexpr int kExecSlots = 8;

enum ExecFlags : uint32_t
{
    ExecPartial = 1,  // a step had no position (the game's own pick ran; its key is logged as 0)
    ExecMixed = 2,    // steps from two graphs
    ExecMulti = 4,    // a step had two or more nodes pending (the order could matter)
    ExecApplied = 8,  // a step ran the file-order pick (two or more pending)
};

struct Execution
{
    uintptr_t walker;
    uint32_t spare;
    uint32_t index;
    uint32_t ordinal;
    uint32_t graphId;
    uint32_t steps;
    uint32_t flags;
    uint32_t differs;  // steps where the file-order pick was not the game's own
    uint32_t applied;  // steps that ran the file-order pick (two or more pending)
    uint32_t used;     // last use, for slot reuse
    unsigned char keys[kExecKeys];
};
thread_local Execution t_exec[kExecSlots];
thread_local uint32_t t_execClock = 0;

void WriteExecution(const Execution& e)
{
    const uint32_t seq = static_cast<uint32_t>(InterlockedIncrement(&g_orderCount)) - 1u;
    uint32_t* record = g_orders[seq & (kOrderCapacity - 1u)];
    record[kOrderWords - 1] = 0;
    MemoryBarrier();
    record[0] = e.spare;
    record[1] = (e.index & 0xFFu) | ((e.flags & 0xFFu) << 8) | ((e.steps < 0xFFFFu ? e.steps : 0xFFFFu) << 16);
    record[2] = e.ordinal;
    record[3] = e.graphId;
    std::memcpy(&record[4], e.keys, kExecKeys);  // words 4-7
    record[8] = (e.differs & 0xFFFFu) | ((e.applied < 0xFFFFu ? e.applied : 0xFFFFu) << 16);
    MemoryBarrier();
    record[kOrderWords - 1] = seq + 1u;
    InterlockedIncrement(&g_orderCounters[OrderLogged]);
}

// True when the walker's phase-0 queue is empty, i.e. this is the execution's first pick. -1 when unreadable.
int QueueEmpty(uintptr_t walker)
{
    __try
    {
        const unsigned char* queue = reinterpret_cast<const unsigned char*>(walker + kWalkerQueue);
        return *reinterpret_cast<const uintptr_t*>(queue + 8) == *reinterpret_cast<const uintptr_t*>(queue + 0x18) ? 1 : 0;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return -1;
    }
}

// Notes one phase-0 step: the position of the node that runs (0 when unknown).
void NoteStep(uintptr_t walker, uint32_t graphId, uint32_t key, uint32_t pending, bool differs, bool applied)
{
    const int first = walker != 0 ? QueueEmpty(walker) : -1;
    if (first < 0)
    {
        InterlockedIncrement(&g_orderCounters[OrderNoWalker]);
        return;
    }

    Execution* slot = nullptr;
    Execution* oldest = &t_exec[0];
    for (int i = 0; i < kExecSlots; ++i)
    {
        if (t_exec[i].walker == walker)
        {
            slot = &t_exec[i];
        }

        if (t_exec[i].used < oldest->used)
        {
            oldest = &t_exec[i];
        }
    }

    if (first == 1)
    {
        if (slot == nullptr)
        {
            slot = oldest;
        }

        if (slot->walker != 0 && slot->steps > 0)
        {
            WriteExecution(*slot);
        }

        std::memset(slot, 0, sizeof(*slot));
        WalkerFacts facts;
        if (!ReadWalker(walker, facts))
        {
            InterlockedIncrement(&g_orderCounters[OrderNoWalker]);
            return;
        }

        AcquireSRWLockExclusive(&g_walkerLock);
        WalkerEntry* entry = FindWalker(walker, facts);
        const uint32_t ordinal = entry != nullptr ? ++entry->executions : 0u;
        ReleaseSRWLockExclusive(&g_walkerLock);
        slot->walker = walker;
        slot->spare = facts.spare;
        slot->index = facts.index;
        slot->ordinal = ordinal;
    }
    else if (slot == nullptr)
    {
        InterlockedIncrement(&g_orderCounters[OrderMidExecution]);  // began before the log was on, or its slot was reused
        return;
    }

    slot->used = ++t_execClock;
    if (graphId != 0)
    {
        if (slot->graphId == 0)
        {
            slot->graphId = graphId;
        }
        else if (slot->graphId != graphId)
        {
            slot->flags |= ExecMixed;
        }
    }

    if (key == 0)
    {
        slot->flags |= ExecPartial;
    }

    if (pending >= 2)
    {
        slot->flags |= ExecMulti;
    }

    if (differs)
    {
        slot->differs++;
    }

    if (applied)
    {
        slot->applied++;
        slot->flags |= ExecApplied;
    }

    if (slot->steps < static_cast<uint32_t>(kExecKeys))
    {
        slot->keys[slot->steps] = static_cast<unsigned char>(key < 0xFFu ? key : 0xFFu);
    }

    slot->steps++;
}

// Called by PickThunk with the pending set's address and the executor's walker. Returns the node to run, or 0 for the
// game's own pick.
uintptr_t __cdecl PickScan(const unsigned char* set, uintptr_t walker)
{
    InterlockedIncrement(&g_orderCounters[OrderPicks]);
    Scan s;
    std::memset(&s, 0, sizeof(s));
    s.bestKey = 0xFFFFFFFFu;
    AcquireSRWLockShared(&g_nodeLock);
    const int fallback = ScanSet(set, s);
    ReleaseSRWLockShared(&g_nodeLock);
    if (fallback >= 0)
    {
        InterlockedIncrement(&g_orderCounters[fallback]);
        NoteStep(walker, 0, 0, 0, false, false);
        return 0;
    }

    if (s.pending < 2)
    {
        InterlockedIncrement(&g_orderCounters[OrderSingle]);
        NoteStep(walker, s.graphId, s.bestKey, s.pending, false, false);
        return 0;
    }

    const bool apply = (g_switches & kSwitchNodeOrder) != 0;
    const bool differs = s.bestKey != s.nativeKey;
    if (differs)
    {
        InterlockedIncrement(&g_orderCounters[OrderDiffers]);
    }

    NoteStep(walker, s.graphId, apply ? s.bestKey : s.nativeKey, s.pending, differs, apply);
    if (!apply)
    {
        return 0;
    }

    InterlockedIncrement(&g_orderCounters[OrderApplied]);
    return s.best;
}

uintptr_t __cdecl PickSelect(const unsigned char* set, uintptr_t walker)
{
    if ((g_switches & (kSwitchNodeOrder | kSwitchOrderLog)) != 0)
    {
        const uintptr_t picked = PickScan(set, walker);
        if (picked != 0)
        {
            return picked;
        }
    }

    return PickFallback(set);
}

uint32_t g_pickContinue = 0x007F55E4;  // the executor after the phase-0 pick

// 0x7F55C3 (phase 0's pick, 33 bytes) jumps here, so ESP is still the executor's frame.
// In: pending set at [esp+0x28], walker at [ebp+0x10]. Out: EBX = [esp+0x18] = chosen node;
// ESI, EDI, EBP and ESP unchanged; EAX, ECX, EDX and flags are written at 0x7F55E4 before read.
__declspec(naked) void PickThunk()
{
    __asm {
        lea eax, [esp + 28h]
        push dword ptr [ebp + 10h]
        push eax
        call PickSelect
        add esp, 8
        mov ebx, eax
        mov dword ptr [esp + 18h], ebx
        jmp dword ptr [g_pickContinue]
    }
}

// ---- item 3: the keyed Random draw ----
//
// A Random node (vtable 0x101FCC8) is evaluated only through its slot +0x24 (0x101FCEC -> 0x7F88D0): thiscall, ECX = the
// node, five stack arguments (the executor's pending data, the walker, the driver's context = driver + 0x44, a fourth
// word, the address of the node's history pointer), `ret 0x14`, nothing returned that the executor reads. It draws at
// 0x7F8951 from the context's generator (context + 0x64 = driver + 0xA8; 0x4FA870, thiscall, a value in [0, 1) in ST0),
// scales it by the weight total and takes the outcome; when that outcome is in the node's no-repeat window it jumps back
// and draws again (0x7F8A56 -> 0x7F8941). SimLink points the slot at RandomEvaluate, which notes the evaluation on this
// thread, and retargets the draw to KeyedRandomDraw, which always makes the game's own draw first (so driver +0xA8
// advances as before) and, with switch bit 3 on, returns KeyedUnit(RandomKey(...)) instead: the driver's spare seed, the
// walker's index, the node's graph ID and key from site A', the walker's boundary count, the node's evaluations by that
// walker since the boundary, and the retry. The candidates, weights, no-repeat rules and the Deterministic branch (no
// draw) stay the game's own. The node's history (four bytes per driver context and node, most recent first, 0x7F empty)
// is set empty when the walker's boundary count moved since the node's last keyed evaluation, so a game that ran an
// extra cycle agrees again from the next boundary.
constexpr size_t kContextRandom = 0x64;  // the context's generator (driver + 0xA8)

struct RandomEntry
{
    uintptr_t walker;
    uintptr_t node;
    uint32_t spare;
    uint32_t boundary;
    uint32_t evaluations;
};
constexpr uint32_t kRandomSlots = 1u << 15;  // a power of two; entries are never removed (reset by spare or boundary)
constexpr uint32_t kRandomProbe = 64;
RandomEntry g_random[kRandomSlots];

// Call with g_walkerLock held exclusively. nullptr when no slot is free within the probe.
RandomEntry* FindRandom(uintptr_t walker, uintptr_t node)
{
    uint32_t slot = static_cast<uint32_t>(((walker >> 3) ^ ((node >> 3) * 40503u)) * 2654435761u) & (kRandomSlots - 1u);
    for (uint32_t probe = 0; probe < kRandomProbe; ++probe, slot = (slot + 1u) & (kRandomSlots - 1u))
    {
        RandomEntry& entry = g_random[slot];
        if (entry.walker == walker && entry.node == node)
        {
            return &entry;
        }

        if (entry.walker == 0)
        {
            entry.walker = walker;
            entry.node = node;
            entry.spare = 0;
            entry.boundary = 0;
            entry.evaluations = 0;
            return &entry;
        }
    }

    return nullptr;
}

// The evaluation in progress on this thread.
struct RandomEval
{
    int active;
    int keyed;
    uintptr_t generator;  // context + 0x64: the draws of this evaluation
    uint32_t draws;
    uint32_t spare;
    uint32_t index;
    uint32_t graphId;
    uint32_t nodeKey;
    uint32_t boundary;
    uint32_t evaluation;
    // For the input log (switch bit 4):
    uint32_t generation;  // the walker's requests (tier 3's count; boundary - generation = its EnterStates)
    uint32_t flags;       // RandomFlag*
    uint32_t history;     // the node's history as the evaluate found it (after a reset), byte 0 the most recent
    float value;          // the value the last draw returned (keyed or the game's own)
};
thread_local RandomEval t_random;

// Why a keyed draw fell back to the game's own (the input log's reason byte).
enum RandomReason : uint32_t
{
    ReasonNone = 0,
    ReasonNoWalker = 1,
    ReasonNoContext = 2,
    ReasonNoSpare = 3,
    ReasonNoKey = 4,
    ReasonFull = 5,
    ReasonException = 6,
};

enum RandomFlag : uint32_t
{
    RandomFlagKeyed = 1,         // the draws returned the keyed value
    RandomFlagReset = 2,         // the history was set empty before the evaluate
    RandomFlagNewBoundary = 4,   // the first evaluation since the walker's boundary moved (a reset when keyed)
    RandomFlagNoOutcome = 8,     // the history could not be read after the evaluate
    RandomFlagSwitchOn = 16,     // switch bit 3 was on (a fallback keeps the game's own value)
    RandomFlagCopied = 32,       // option C: the walker's counters were copied and still stand at the copied boundary
};

uintptr_t ReadNodeGraph(uintptr_t node)
{
    __try
    {
        return *reinterpret_cast<const uintptr_t*>(node + kNodeGraph);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}

// The node's four history bytes as one word (byte 0 the most recent); 0x7F7F7F7F while not allocated. False on an
// access violation.
bool ReadHistory(uintptr_t historySlot, uint32_t& history)
{
    __try
    {
        const unsigned char* bytes = *reinterpret_cast<const unsigned char* const*>(historySlot);
        history = bytes == nullptr ? 0x7F7F7F7Fu : *reinterpret_cast<const uint32_t*>(bytes);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// Sets the node's history empty (0x7F x 4, as the evaluate allocates it). A null pointer is a history not allocated
// yet, which the evaluate allocates empty. False on an access violation.
bool ResetHistory(uintptr_t historySlot)
{
    __try
    {
        unsigned char* history = *reinterpret_cast<unsigned char* const*>(historySlot);
        if (history != nullptr)
        {
            history[0] = history[1] = history[2] = history[3] = 0x7F;
        }

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// With switch bit 3 or 4 on: fills e's key fields as far as they are known. With bit 3 (keyed) it also sets the history
// empty at a new boundary and sets e.keyed; with bit 4 alone (the input log) it changes nothing in the game. Returns
// why the key is incomplete (ReasonNone when whole); the counters count the fallbacks only with bit 3.
uint32_t PrepareKeyed(RandomEval& e, uintptr_t node, uintptr_t walker, uintptr_t context, uintptr_t historySlot,
                      bool keyed)
{
    if (keyed)
    {
        e.flags |= RandomFlagSwitchOn;
    }

    WalkerFacts facts;
    if (!ReadWalker(walker, facts))
    {
        if (keyed)
        {
            InterlockedIncrement(&g_randomCounters[RandomNoWalker]);
        }

        return ReasonNoWalker;
    }

    e.spare = facts.spare;
    e.index = facts.index;
    if (context != facts.driver + kDriverContext)
    {
        if (keyed)
        {
            InterlockedIncrement(&g_randomCounters[RandomNoContext]);
        }

        return ReasonNoContext;
    }

    if (facts.spare == 0)
    {
        if (keyed)
        {
            InterlockedIncrement(&g_randomCounters[RandomNoSpare]);
        }

        return ReasonNoSpare;
    }

    uint32_t nodeKey = 0;
    uint32_t graphId = 0;
    const uintptr_t graph = ReadNodeGraph(node);
    AcquireSRWLockShared(&g_nodeLock);
    const GraphNode* known = FindNode(node);
    if (known != nullptr && graph != 0 && known->graph == graph)
    {
        nodeKey = known->key;
        graphId = known->graphId;
    }

    ReleaseSRWLockShared(&g_nodeLock);
    if (nodeKey == 0)
    {
        if (keyed)
        {
            InterlockedIncrement(&g_randomCounters[RandomNoKey]);
        }

        return ReasonNoKey;
    }

    e.graphId = graphId;
    e.nodeKey = nodeKey;
    bool reset = false;
    uint32_t boundary = 0;
    uint32_t evaluation = 0;
    uint32_t generation = 0;
    bool copied = false;
    AcquireSRWLockExclusive(&g_walkerLock);
    WalkerEntry* entry = FindWalker(walker, facts);
    RandomEntry* slot = entry != nullptr ? FindRandom(walker, node) : nullptr;
    if (slot != nullptr)
    {
        boundary = entry->boundaries;
        generation = entry->generation;
        copied = InCopiedWindow(*entry);
        if (slot->spare != facts.spare || slot->boundary != boundary)
        {
            slot->spare = facts.spare;
            slot->boundary = boundary;
            slot->evaluations = 0;
            reset = true;
        }

        evaluation = slot->evaluations++;
    }

    ReleaseSRWLockExclusive(&g_walkerLock);
    if (slot == nullptr)
    {
        if (keyed)
        {
            InterlockedIncrement(&g_randomCounters[RandomFull]);
        }

        return ReasonFull;
    }

    e.boundary = boundary;
    e.evaluation = evaluation;
    e.generation = generation;
    if (reset)
    {
        e.flags |= RandomFlagNewBoundary;
    }

    if (copied)
    {
        // Option C's residual: this node's evaluations and history were not copied, so until the walker's next
        // boundary this evaluate's key and window are the copy's own.
        e.flags |= RandomFlagCopied;
        InterlockedIncrement(&g_walkerCounters[WalkerResidualEvals]);
    }

    if (keyed && reset)
    {
        if (!ResetHistory(historySlot))
        {
            InterlockedIncrement(&g_randomCounters[RandomException]);
            return ReasonException;
        }

        e.flags |= RandomFlagReset;
        InterlockedIncrement(&g_randomCounters[RandomResets]);
    }

    if (keyed)
    {
        e.flags |= RandomFlagKeyed;
        e.keyed = 1;
    }

    return ReasonNone;
}

// ---- item 3's input log (switch bit 4) ----

volatile LONG g_randomLogCount = 0;
uint32_t g_randomLog[kRandomCapacity][kRandomWords];

void WriteRandomRecord(const RandomEval& e, uint32_t reason, uint32_t outcome)
{
    const uint32_t seq = static_cast<uint32_t>(InterlockedIncrement(&g_randomLogCount)) - 1u;
    uint32_t* record = g_randomLog[seq & (kRandomCapacity - 1u)];
    record[kRandomWords - 1] = 0;
    MemoryBarrier();
    record[0] = e.spare;
    record[1] = (e.index & 0xFFu) | ((e.flags & 0xFFu) << 8) | ((e.draws < 0xFFFFu ? e.draws : 0xFFFFu) << 16);
    record[2] = e.graphId;
    record[3] = (e.nodeKey & 0xFFFFu) | ((outcome & 0xFFu) << 16) | ((reason & 0xFFu) << 24);
    record[4] = e.boundary;
    record[5] = e.evaluation;
    record[6] = e.generation;
    std::memcpy(&record[7], &e.value, 4);
    record[8] = e.history;
    MemoryBarrier();
    record[kRandomWords - 1] = seq + 1u;
    InterlockedIncrement(&g_randomCounters[RandomLogged]);
}

// As __fastcall the unused EDX is the second parameter and the stack arguments follow; EAX is passed through.
typedef uint32_t(__fastcall* RandomEvaluateFn)(void* node, void* edx, uintptr_t pending, uintptr_t walker,
                                               uintptr_t context, uintptr_t word, uintptr_t historySlot);
const RandomEvaluateFn kOriginalRandomEvaluate = reinterpret_cast<RandomEvaluateFn>(0x007F88D0);

uint32_t __fastcall RandomEvaluate(void* node, void* edx, uintptr_t pending, uintptr_t walker, uintptr_t context,
                                   uintptr_t word, uintptr_t historySlot)
{
    InterlockedIncrement(&g_randomCounters[RandomEvaluations]);
    const RandomEval outer = t_random;  // an evaluate does not nest; restored anyway
    RandomEval& e = t_random;
    std::memset(&e, 0, sizeof(e));
    e.active = 1;
    e.generator = context + kContextRandom;
    const LONG switches = g_switches;
    if ((switches & (kSwitchKeyedRandom | kSwitchRandomLog)) == 0)
    {
        const uint32_t result = kOriginalRandomEvaluate(node, edx, pending, walker, context, word, historySlot);
        t_random = outer;
        return result;
    }

    const uint32_t reason = PrepareKeyed(e, reinterpret_cast<uintptr_t>(node), walker, context, historySlot,
                                         (switches & kSwitchKeyedRandom) != 0);
    const bool log = (switches & kSwitchRandomLog) != 0;
    if (log && !ReadHistory(historySlot, e.history))
    {
        e.history = 0xFFFFFFFFu;
    }

    const uint32_t result = kOriginalRandomEvaluate(node, edx, pending, walker, context, word, historySlot);
    if (log)
    {
        uint32_t after = 0;
        if (!ReadHistory(historySlot, after))
        {
            e.flags |= RandomFlagNoOutcome;
            after = 0xFFu;
        }

        WriteRandomRecord(e, reason, after & 0xFFu);
    }

    t_random = outer;
    return result;
}

// 0x4FA870 returns int32(x) * 2^-32 + 0.5 (0 if that rounds to 1) under the game's x87 precision: at most 33 significant
// bits, so a double holds the game's own value exactly. The keyed value has 24 bits, so the caller's `fmul` by the
// float32 total and its float32 store round once to the same float whatever the precision control.
typedef double(__fastcall* RandomDrawFn)(uint32_t* generator, void* edx);  // thiscall, no stack arguments, ST0
const RandomDrawFn kOriginalRandomDraw = reinterpret_cast<RandomDrawFn>(0x004FA870);

double __fastcall KeyedRandomDraw(uint32_t* generator, void*)
{
    const double native = kOriginalRandomDraw(generator, nullptr);  // the game's own draw: driver +0xA8 advances
    RandomEval& e = t_random;
    if (!e.active || reinterpret_cast<uintptr_t>(generator) != e.generator)
    {
        InterlockedIncrement(&g_randomCounters[RandomOutside]);
        return native;
    }

    InterlockedIncrement(&g_randomCounters[RandomDrawsIn]);
    const uint32_t retry = e.draws++;
    if (retry > 0)
    {
        InterlockedIncrement(&g_randomCounters[RandomRetries]);
    }

    if (!e.keyed)
    {
        e.value = static_cast<float>(native);
        return native;
    }

    InterlockedIncrement(&g_randomCounters[RandomKeyed]);
    e.value = KeyedUnit(RandomKey(e.spare, e.index, e.graphId, e.nodeKey, e.boundary, e.evaluation, retry));
    return e.value;
}

// ---- install ----

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

constexpr size_t kPickBlockLength = 33;

// Pinned build verification digests (128-bit SiteDigest) for Steam 1.67.2.024037 TS3W.exe sites.
const Check kChecks[] = {
    {0x00803D68, 23, {0x7CFCE4224959E041ULL, 0x9711760E22772CD2ULL}},  // walker step query setup + call
    {0x00803137, 26, {0x9A6A4DB2F9DF2E62ULL, 0x7587329D561698BDULL}},  // request planning query setup + call
    {0x00802EC5, 5, {0x1BE91F7006B51543ULL, 0x31AA231E46C75A1CULL}},   // request walker EDI setup
    {0x007FCB55, 9, {0x799C1786442A5FB8ULL, 0x116B17FBB31FEEF5ULL}},   // draw all-paths call site
    {0x007FCC2C, 17, {0xE01ED251463061D2ULL, 0x26A11848ACA09F8AULL}},  // draw shortest-paths call site
    {0x00804433, 15, {0x4D36ACA48C3812B3ULL, 0x994F251BFF666CDDULL}},  // request dispatch call site 1
    {0x00804478, 15, {0x84274211CA041B40ULL, 0xA203F1FF63FC943BULL}},  // request dispatch call site 2
    {0x007FCE00, 16, {0x5C3E90A7C0A25276ULL, 0xE2C654E349B8B2F1ULL}},  // path-finder query entry
    {0x004FA840, 35, {0x56498D47CA89B04EULL, 0x87AB916C404ADB4AULL}},  // path-finder LCG draw entry
    {0x00802E90, 13, {0x456401CA5E2D2BF2ULL, 0x9F1A4B551576A895ULL}},  // request dispatch entry
    {0x007F55C3, kPickBlockLength, {0xB88FF2696BA133D5ULL, 0x655A107F70D3B2E5ULL}},  // site A' phase-0 pick block
    {0x007F555B, 2, {0xED7E18DB7B71A4BCULL, 0x0403A407AC754749ULL}},   // site A' pick entry branch
    {0x007F55E4, 3, {0x4A0C827F1E67D0A7ULL, 0x7CD4A729EF08B0E4ULL}},   // site A' pick continuation
    {0x007F5531, 13, {0x21008A3639D73B9EULL, 0xC5100E80B88AF4E6ULL}},  // site A' pending set setup
    {0x0101F528, 4, {0x715D0C14A38747BFULL, 0xB6CED47C57F0655BULL}},   // DecisionGraph factory load slot
    {0x007FDF40, 27, {0x6A2A639DA4FFC617ULL, 0x80E64F1257DC1A9AULL}},  // DecisionGraph factory load entry
    {0x0101FCEC, 4, {0xA328848A7A5376C8ULL, 0x5289C9D02B755EE6ULL}},   // Random node evaluate slot
    {0x007F88D0, 14, {0xF9D4F6282515719EULL, 0x325F454169BA3A3CULL}},  // Random node evaluate entry
    {0x007F894A, 12, {0x85A470AD8B05B194ULL, 0x447CD8AA5DD9AA48ULL}},  // Random node draw call site
    {0x007F8A50, 12, {0xBE4028F87BB37376ULL, 0x00169BBB0C06F1D8ULL}},  // Random node no-repeat retry jump
    {0x004FA870, 18, {0x47ED4C4972E45240ULL, 0xCBF673C12B066BB7ULL}},  // Random node float generator entry
    {0x00804255, 12, {0x1D321F2479165C10ULL, 0x8904225DF95A2DBBULL}},  // EnterState call site 1
    {0x00804273, 9, {0x3EFCA0388AAFC665ULL, 0x3230ACEC95EAF6FCULL}},   // EnterState call site 2
    {0x00802E00, 12, {0x62068E338D1A3F2EULL, 0xD3930E2D85E756D0ULL}},  // EnterState entry
};

// Site A': a block replaced by `jmp rel32` to its thunk and nops, and a vtable slot (read-only data) repointed.
struct BlockJump
{
    uintptr_t va;
    size_t length;
    const void* target;
};

struct SlotPatch
{
    uintptr_t va;
    uintptr_t original;  // the pointer the slot must hold (also verified in kChecks)
    const void* target;
};

const BlockJump kBlockJumps[] = {
    {0x007F55C3, kPickBlockLength, reinterpret_cast<const void*>(&PickThunk)},
};

const SlotPatch kSlotPatches[] = {
    {0x0101F528, 0x007FDF40, reinterpret_cast<const void*>(&GraphLoad)},
    {0x0101FCEC, 0x007F88D0, reinterpret_cast<const void*>(&RandomEvaluate)},
};

const Retarget kRetargets[] = {
    {0x00803D7A, reinterpret_cast<const void*>(&QueryWalkThunk)},
    {0x0080314C, reinterpret_cast<const void*>(&QueryRequestThunk)},
    {0x007FCB59, reinterpret_cast<const void*>(&DrawAllPaths)},
    {0x007FCC38, reinterpret_cast<const void*>(&DrawShortestPaths)},
    {0x0080443D, reinterpret_cast<const void*>(&RequestThunk)},
    {0x00804482, reinterpret_cast<const void*>(&RequestThunk)},
    {0x007F8951, reinterpret_cast<const void*>(&KeyedRandomDraw)},
    {0x0080425C, reinterpret_cast<const void*>(&EnterThunk)},
    {0x00804277, reinterpret_cast<const void*>(&EnterThunk)},
};

bool KillSwitch(HMODULE self)
{
    wchar_t value[8];
    if (GetEnvironmentVariableW(L"SIMLINK_NOPATCH", value, 8) > 0)
    {
        return true;
    }

    wchar_t path[MAX_PATH];
    const DWORD length = GetModuleFileNameW(self, path, MAX_PATH);
    wchar_t* slash = length > 0 && length < MAX_PATH ? wcsrchr(path, L'\\') : nullptr;
    if (slash == nullptr || static_cast<size_t>(slash - path) + 20 >= MAX_PATH)
    {
        return false;
    }

    wcscpy_s(slash + 1, MAX_PATH - static_cast<size_t>(slash + 1 - path), L"SimLink.nopatch");
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

void SetState(PatchState state, LONG detail)
{
    InterlockedExchange(&g_detail, detail);
    InterlockedExchange(&g_state, state);
}

void InstallChecked()
{
    if (!RegistrarBytesMatch())
    {
        SetState(PatchBuildMismatch, 0);
        return;
    }

    for (size_t i = 0; i < sizeof(kChecks) / sizeof(kChecks[0]); ++i)
    {
        if (!SiteDigestMatches(kChecks[i].va, kChecks[i].length, kChecks[i].digest))
        {
            SetState(PatchSiteMismatch, static_cast<LONG>(i + 1));
            return;
        }
    }

    for (size_t i = 0; i < sizeof(kRetargets) / sizeof(kRetargets[0]); ++i)
    {
        unsigned char* call = reinterpret_cast<unsigned char*>(kRetargets[i].call);
        DWORD old = 0;
        if (call[0] != 0xE8 || !VirtualProtect(call, 5, PAGE_EXECUTE_READWRITE, &old))
        {
            SetState(PatchProtectFailed, static_cast<LONG>(i + 1));
            return;
        }

        const int32_t rel = static_cast<int32_t>(reinterpret_cast<uintptr_t>(kRetargets[i].target) -
                                                 (kRetargets[i].call + 5));
        std::memcpy(call + 1, &rel, 4);
        DWORD unused = 0;
        VirtualProtect(call, 5, old, &unused);
        FlushInstructionCache(GetCurrentProcess(), call, 5);
    }

    const size_t retargets = sizeof(kRetargets) / sizeof(kRetargets[0]);
    for (size_t i = 0; i < sizeof(kBlockJumps) / sizeof(kBlockJumps[0]); ++i)
    {
        unsigned char* block = reinterpret_cast<unsigned char*>(kBlockJumps[i].va);
        const size_t length = kBlockJumps[i].length;
        DWORD old = 0;
        if (length < 5 || !VirtualProtect(block, length, PAGE_EXECUTE_READWRITE, &old))
        {
            SetState(PatchProtectFailed, static_cast<LONG>(retargets + i + 1));
            return;
        }

        const int32_t rel = static_cast<int32_t>(reinterpret_cast<uintptr_t>(kBlockJumps[i].target) - (kBlockJumps[i].va + 5));
        std::memset(block + 5, 0x90, length - 5);
        std::memcpy(block + 1, &rel, 4);
        block[0] = 0xE9;
        DWORD unused = 0;
        VirtualProtect(block, length, old, &unused);
        FlushInstructionCache(GetCurrentProcess(), block, length);
    }

    const size_t blocks = sizeof(kBlockJumps) / sizeof(kBlockJumps[0]);
    for (size_t i = 0; i < sizeof(kSlotPatches) / sizeof(kSlotPatches[0]); ++i)
    {
        void** slot = reinterpret_cast<void**>(kSlotPatches[i].va);
        DWORD old = 0;
        if (reinterpret_cast<uintptr_t>(*slot) != kSlotPatches[i].original ||
            !VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old))
        {
            SetState(PatchProtectFailed, static_cast<LONG>(retargets + blocks + i + 1));
            return;
        }

        InterlockedExchangePointer(slot, const_cast<void*>(kSlotPatches[i].target));
        DWORD unused = 0;
        VirtualProtect(slot, sizeof(void*), old, &unused);
    }

    SetState(PatchInstalled, 0);
}
}  // namespace

void Install(HMODULE self)
{
    if (KillSwitch(self))
    {
        SetState(PatchKillSwitch, 0);
        return;
    }

    __try
    {
        InstallChecked();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        SetState(PatchException, 0);
    }
}

int32_t PatchStateWord()
{
    return static_cast<int32_t>(g_state) | (static_cast<int32_t>(g_detail) << 8);
}

int __cdecl SetSwitches(int mask)
{
    return mask < 0 ? static_cast<int>(g_switches) : static_cast<int>(InterlockedExchange(&g_switches, mask));
}

int __cdecl PatchStateIcall()
{
    return PatchStateWord();
}

int __cdecl PickCount()
{
    return static_cast<int>(g_pickCount);
}

int __cdecl PickWord(int seq, int word)
{
    if (seq < 0 || word < 0 || word >= kPickWords)
    {
        return 0;
    }

    return static_cast<int>(g_picks[static_cast<uint32_t>(seq) & (kPickCapacity - 1u)][word]);
}

int __cdecl PickCounter(int which)
{
    return which >= 0 && which < CounterCount ? static_cast<int>(g_counters[which]) : 0;
}

int __cdecl OrderCount()
{
    return static_cast<int>(g_orderCount);
}

int __cdecl OrderWord(int seq, int word)
{
    if (seq < 0 || word < 0 || word >= kOrderWords)
    {
        return 0;
    }

    return static_cast<int>(g_orders[static_cast<uint32_t>(seq) & (kOrderCapacity - 1u)][word]);
}

int __cdecl OrderCounter(int which)
{
    return which >= 0 && which < OrderCounterCount ? static_cast<int>(g_orderCounters[which]) : 0;
}

int __cdecl RandomCounter(int which)
{
    return which >= 0 && which < RandomCounterCount ? static_cast<int>(g_randomCounters[which]) : 0;
}

int __cdecl RandomLogCount()
{
    return static_cast<int>(g_randomLogCount);
}

int __cdecl RandomLogWord(int seq, int word)
{
    if (seq < 0 || word < 0 || word >= kRandomWords)
    {
        return 0;
    }

    return static_cast<int>(g_randomLog[static_cast<uint32_t>(seq) & (kRandomCapacity - 1u)][word]);
}

// ---- option C: walker counters copied to a client's copy (native version 13; walker_copy_logic.h) ----
//
// The server reads each walker of a driver it introduced to a client (WalkerRead, without creating or resetting an
// entry) and rings AW| from the mod's bridge tick; the client writes the counts onto its copy's walker of the same index
// (WalkerWrite), under the spare they were read with, or keeps them until SimLink first meets that walker under that
// spare. A copy re-acquired after kProxyInvalid gets its old driver's counts (WalkerCarry), with no word from the server.

namespace
{
constexpr uint32_t kMaxWalkers = 64;  // ReadWalker's bound on a driver's walker list

enum WalkerResult : int
{
    WalkerResultBytesMismatch = -1,
    WalkerResultException = -2,
    WalkerResultNoDriver = -3,
    WalkerResultBadIndex = -4,
    WalkerResultNoOld = -5,
    WalkerResultTableFull = -6,
};

uint32_t g_walkerRead[6];  // the last WalkerRead (the script thread only)
// Optional diagnostic selector -1; ordinary walker counter snapshots are unchanged.
constexpr int kRequestSnapshot = 0x19500000;
constexpr int kRequestWordBase = 1000;
constexpr int kRequestStride = 18;
uint32_t g_requestRead[3 + kMaxWalkers * kRequestStride];
HandleEntry g_handles[kHandleSlots];
SRWLOCK g_handleLock = SRWLOCK_INIT;

// The driver's spare seed and walker pointers (count of them). An empty walker list is a driver whose walkers do not
// exist yet. False on an access violation or a list that does not look like one.
bool ReadWalkerList(const unsigned char* driver, uint32_t& spare, uintptr_t* walkers, uint32_t& count)
{
    __try
    {
        count = 0;
        spare = *reinterpret_cast<const volatile uint32_t*>(driver + kDriverSpareSeed);
        const unsigned char* begin = *reinterpret_cast<unsigned char* const*>(driver + kDriverWalkersBegin);
        const unsigned char* end = *reinterpret_cast<unsigned char* const*>(driver + kDriverWalkersEnd);
        if (begin == nullptr)
        {
            return end == nullptr;
        }

        if (end < begin || end - begin > static_cast<ptrdiff_t>(8 * kMaxWalkers))
        {
            return false;
        }

        count = static_cast<uint32_t>((end - begin) / 8);
        for (uint32_t i = 0; i < count; ++i)
        {
            walkers[i] = *reinterpret_cast<const uintptr_t*>(begin + 8 * i + 4);
        }

        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// The handle's driver, its spare seed and its walkers; a WalkerResult (< 0) when there is none.
int ReadDriver(int handle, unsigned char*& driver, uint32_t& spare, uintptr_t* walkers, uint32_t& count)
{
    driver = nullptr;
    const int resolved = ResolveSeedDriver(static_cast<uint32_t>(handle), &driver);
    if (resolved != 1)
    {
        if (resolved == 0)
        {
            InterlockedIncrement(&g_walkerCounters[WalkerNoDriver]);
            return WalkerResultNoDriver;
        }

        return resolved == -1 ? WalkerResultBytesMismatch : WalkerResultException;
    }

    if (!ReadWalkerList(driver, spare, walkers, count))
    {
        InterlockedIncrement(&g_walkerCounters[WalkerExceptions]);
        return WalkerResultException;
    }

    return 1;
}

// Puts counts on walker index of driver (its spare and walkers just read): now, or kept until SimLink meets that walker
// under spare (Place). Returns the Placement, or WalkerResultTableFull.
int PlaceCounts(uintptr_t driver, uint32_t index, uint32_t spare, const WalkerCounts& counts, uint32_t driverSpare,
                const uintptr_t* walkers, uint32_t count)
{
    int placement = Place(index < count, driverSpare, spare);
    AcquireSRWLockExclusive(&g_walkerLock);
    if (placement == PlaceWritten)
    {
        const WalkerFacts facts = {driverSpare, index, driver};
        WalkerEntry* entry = FindWalker(walkers[index], facts);
        if (entry != nullptr)
        {
            SetCounts(*entry, counts);
            Forget(g_pending, driver, index);
        }
        else
        {
            placement = WalkerResultTableFull;
        }
    }
    else
    {
        Stage(g_pending, driver, index, spare, counts);
    }

    ReleaseSRWLockExclusive(&g_walkerLock);
    switch (placement)
    {
    case PlaceWritten:
        InterlockedIncrement(&g_walkerCounters[WalkerWritten]);
        InterlockedIncrement(&g_walkerCounters[WalkerWindows]);
        break;
    case PlacePending:
        InterlockedIncrement(&g_walkerCounters[WalkerStaged]);
        break;
    case PlaceSpareDiffers:
        InterlockedIncrement(&g_walkerCounters[WalkerSpareDiffers]);
        break;
    default:
        InterlockedIncrement(&g_walkerCounters[WalkerTableFull]);
        break;
    }

    return placement;
}
}  // namespace

void NoteResolved(uint32_t handle, uintptr_t driver)
{
    AcquireSRWLockExclusive(&g_handleLock);
    NoteHandle(g_handles, handle, driver);
    ReleaseSRWLockExclusive(&g_handleLock);
}

// Read-only facts from 0x8042d0 (request validation), 0x802d20 and 0x801150 (deque).
// Bounded to four queued requests per actor, with the full queue count retained.
int ReadRequestSnapshot(int handle)
{
    unsigned char* driver = nullptr;
    uint32_t spare = 0;
    uintptr_t walkers[kMaxWalkers];
    uint32_t count = 0;
    const int read = ReadDriver(handle, driver, spare, walkers, count);
    if (read != 1)
    {
        return read;
    }
    __try
    {
        std::memset(g_requestRead, 0, sizeof(g_requestRead));
        g_requestRead[0] = driver[0x145];
        g_requestRead[1] = driver[0x3c];
        g_requestRead[2] = count;
        const unsigned char* actors = *reinterpret_cast<unsigned char* const*>(driver + 0x10);
        for (uint32_t i = 0; i < count; ++i)
        {
            uint32_t* out = g_requestRead + 3 + i * kRequestStride;
            out[0] = *reinterpret_cast<const uint32_t*>(actors + 8 * i);
            const unsigned char* walker = reinterpret_cast<const unsigned char*>(walkers[i]);
            out[1] = static_cast<uint32_t>(walkers[i]);
            if (walker == nullptr)
            {
                continue;
            }
            out[2] = *reinterpret_cast<const uint32_t*>(walker + 0x10);
            out[3] = out[2] == 0 ? 0 : *reinterpret_cast<const uint32_t*>(out[2] + 0x28);
            out[4] = *reinterpret_cast<const uint32_t*>(walker + 0x1c);
            out[5] = out[4] == 0 ? 0 : *reinterpret_cast<const uint32_t*>(out[4] + 0x28);
            out[6] = *reinterpret_cast<const uint32_t*>(walker + 0x24);
            out[6] = out[6] == 0 ? 0 : *reinterpret_cast<const uint32_t*>(out[6] + 0x28);
            out[7] = *reinterpret_cast<const uint32_t*>(walker + 0x20);
            out[8] = *reinterpret_cast<const uint32_t*>(walker + 0x6c);
            const unsigned char* cursor = *reinterpret_cast<unsigned char* const*>(walker + 0x34);
            const unsigned char* end = *reinterpret_cast<unsigned char* const*>(walker + 0x44);
            if (cursor == end)
            {
                continue;
            }
            const uintptr_t firstNode = *reinterpret_cast<const uintptr_t*>(walker + 0x40);
            const uintptr_t lastNode = *reinterpret_cast<const uintptr_t*>(walker + 0x50);
            if (lastNode < firstNode || lastNode - firstNode > 0x10000)
            {
                return WalkerResultException;
            }
            const uintptr_t first = *reinterpret_cast<const uintptr_t*>(walker + 0x38);
            const uintptr_t last = *reinterpret_cast<const uintptr_t*>(walker + 0x48);
            out[9] = static_cast<uint32_t>((lastNode - firstNode) / 4 * 32 +
                (reinterpret_cast<uintptr_t>(end) - last) / 8 -
                (reinterpret_cast<uintptr_t>(cursor) - first) / 8);
            uintptr_t node = firstNode;
            const unsigned char* blockEnd = *reinterpret_cast<unsigned char* const*>(walker + 0x3c);
            for (uint32_t q = 0; q < 4 && q < out[9]; ++q)
            {
                out[10 + 2 * q] = *reinterpret_cast<const uint32_t*>(cursor);
                const uintptr_t state = *reinterpret_cast<const uintptr_t*>(cursor + 4);
                out[11 + 2 * q] = state == 0 ? 0 : *reinterpret_cast<const uint32_t*>(state + 0x28);
                cursor += 8;
                if (cursor == blockEnd && q + 1 < out[9])
                {
                    node += 4;
                    cursor = *reinterpret_cast<unsigned char* const*>(node);
                    blockEnd = cursor + 0x100;
                }
            }
        }
        return kRequestSnapshot | static_cast<int>(count);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return WalkerResultException;
    }
}

int __cdecl WalkerRead(int handle, int index)
{
    if (index == -1)
    {
        return ReadRequestSnapshot(handle);
    }
    if (index < 0 || index >= static_cast<int>(kMaxWalkers))
    {
        return WalkerResultBadIndex;
    }

    unsigned char* driver = nullptr;
    uint32_t spare = 0;
    uintptr_t walkers[kMaxWalkers];
    uint32_t count = 0;
    const int read = ReadDriver(handle, driver, spare, walkers, count);
    if (read != 1)
    {
        return read;
    }

    if (static_cast<uint32_t>(index) >= count)
    {
        return 0;
    }

    AcquireSRWLockShared(&g_walkerLock);
    const WalkerEntry* entry = LookWalker(walkers[index]);
    const bool counted = entry != nullptr && entry->spare == spare;  // another spare: FindWalker zeroes it at its next call
    g_walkerRead[0] = spare;
    g_walkerRead[1] = counted ? entry->generation : 0u;
    g_walkerRead[2] = counted ? entry->draws : 0u;
    g_walkerRead[3] = counted ? entry->boundaries : 0u;
    g_walkerRead[4] = counted ? entry->executions : 0u;
    g_walkerRead[5] = counted ? 1u : 0u;
    ReleaseSRWLockShared(&g_walkerLock);
    InterlockedIncrement(&g_walkerCounters[WalkerReads]);
    return static_cast<int>(count);
}

int __cdecl WalkerWord(int word)
{
    if (word >= kRequestWordBase && word - kRequestWordBase < static_cast<int>(sizeof(g_requestRead) / sizeof(g_requestRead[0])))
    {
        return static_cast<int>(g_requestRead[word - kRequestWordBase]);
    }
    return word >= 0 && word < 6 ? static_cast<int>(g_walkerRead[word]) : 0;
}

int __cdecl WalkerWrite(int handle, int index, int spare, int generation, int draws, int boundaries, int executions)
{
    if (index < 0 || index >= static_cast<int>(kMaxWalkers))
    {
        return WalkerResultBadIndex;
    }

    unsigned char* driver = nullptr;
    uint32_t driverSpare = 0;
    uintptr_t walkers[kMaxWalkers];
    uint32_t count = 0;
    const int read = ReadDriver(handle, driver, driverSpare, walkers, count);
    if (read != 1)
    {
        return read;
    }

    const WalkerCounts counts = {static_cast<uint32_t>(generation), static_cast<uint32_t>(draws),
                                 static_cast<uint32_t>(boundaries), static_cast<uint32_t>(executions)};
    return PlaceCounts(reinterpret_cast<uintptr_t>(driver), static_cast<uint32_t>(index), static_cast<uint32_t>(spare),
                       counts, driverSpare, walkers, count);
}

int __cdecl WalkerCarry(int oldHandle, int newHandle, int spare)
{
    AcquireSRWLockShared(&g_handleLock);
    const uintptr_t oldDriver = DriverOfHandle(g_handles, static_cast<uint32_t>(oldHandle));
    ReleaseSRWLockShared(&g_handleLock);
    if (oldDriver == 0u)
    {
        InterlockedIncrement(&g_walkerCounters[WalkerCarryNoOld]);
        return WalkerResultNoOld;
    }

    unsigned char* driver = nullptr;
    uint32_t driverSpare = 0;
    uintptr_t walkers[kMaxWalkers];
    uint32_t count = 0;
    const int read = ReadDriver(newHandle, driver, driverSpare, walkers, count);
    if (read != 1)
    {
        return read;
    }

    bool found[kMaxWalkers];
    WalkerCounts counts[kMaxWalkers];
    uintptr_t source[kMaxWalkers];
    AcquireSRWLockShared(&g_walkerLock);
    CarryCollect(g_walkers, kWalkerSlots, oldDriver, static_cast<uint32_t>(spare), found, counts, source, kMaxWalkers);
    ReleaseSRWLockShared(&g_walkerLock);
    InterlockedIncrement(&g_walkerCounters[WalkerCarries]);
    int carried = 0;
    for (uint32_t i = 0; i < kMaxWalkers; ++i)
    {
        if (found[i] && PlaceCounts(reinterpret_cast<uintptr_t>(driver), i, static_cast<uint32_t>(spare), counts[i],
                                    driverSpare, walkers, count) > 0)
        {
            ++carried;
        }
    }

    InterlockedExchangeAdd(&g_walkerCounters[WalkerCarried], carried);
    return carried;
}

int __cdecl WalkerCounter(int which)
{
    switch (which)
    {
    case WalkerPendingStaged:
        return static_cast<int>(g_pending.staged);
    case WalkerPendingReplaced:
        return static_cast<int>(g_pending.replaced);
    case WalkerPendingTaken:
        return static_cast<int>(g_pending.taken);
    case WalkerPendingEvicted:
        return static_cast<int>(g_pending.evicted);
    default:
        return which >= 0 && which < WalkerCounterCount ? static_cast<int>(g_walkerCounters[which]) : 0;
    }
}
}  // namespace sacs
}  // namespace simlink
