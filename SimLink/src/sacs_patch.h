// tier 3: keyed path picks (Steam 1.67.2.024037 TS3W.exe). SimLink retargets six 5-byte
// `call rel32` sites in TS3W.exe at load, after checking every site's digest (and the build's registrar digest); any
// mismatch, or the kill switch (environment variable SIMLINK_NOPATCH, or a file SimLink.nopatch beside the ASI),
// leaves the game unpatched. Patched or not, the game behaves as before until the mod sets switch bit 0 from the
// tunable sacs.keyed-picks: until then every draw is the game's own, and the thunks only log the picks.
//
// Site A' ("Site A': design"), installed with tier 3 (all or nothing): the decision-graph executor's phase-0 pick
// (0x7F55C3, 33 bytes) jumps to a thunk, and the DecisionGraph factory's load slot (0x101F528) points at a wrapper
// that records each loaded node's position in its graph's list 1. With switch bit 1 on, the thunk runs the pending
// node with the smallest position; otherwise, or when any pending node has no position, the game's own pick.
#pragma once

#include <windows.h>

#include <cstdint>

namespace simlink
{
namespace sacs
{
enum PatchState : int32_t
{
    PatchNotAttempted = 0,
    PatchInstalled = 1,
    PatchKillSwitch = 2,
    PatchBuildMismatch = 3,  // the registrar bytes differ: not the Steam 1.67.2.024037 build
    PatchSiteMismatch = 4,   // detail = site index + 1
    PatchProtectFailed = 5,  // detail = site index + 1
    PatchException = 6,
};

// Switch bits (SetSwitches): read by the thunks on every pick.
constexpr int32_t kSwitchKeyedPicks = 1;  // tier 3: replace the path finder's draw with the keyed draw
constexpr int32_t kSwitchNodeOrder = 2;   // site A': run a decision graph's pending nodes in file order
constexpr int32_t kSwitchOrderLog = 4;    // site A': scan and log the pending set without changing the pick
constexpr int32_t kSwitchKeyedRandom = 8; // item 3: replace a Random node's draw with the keyed draw
constexpr int32_t kSwitchRandomLog = 16;  // item 3: log each Random evaluate's key fields and outcome (changes nothing)

// Called once from DllMain (process attach, TS3W.exe only). Never throws.
void Install(HMODULE self);

// state | detail << 8
int32_t PatchStateWord();

// The pick log: one record per path-finder draw made inside a keyed query site (walker step or request), whatever
// the switch. kPickWords words per record, the last one the commit word (seq + 1 once the record is complete).
constexpr int kPickWords = 10;
constexpr uint32_t kPickCapacity = 16384;  // a power of two

// ---- internal calls (cdecl) ----
int __cdecl SetSwitches(int mask);  // mask < 0: query only. Returns the previous mask.
int __cdecl PatchStateIcall();
int __cdecl PickCount();                 // records written so far (the next seq)
int __cdecl PickWord(int seq, int word); // a record's word; the commit word is seq + 1 when the record is whole
int __cdecl PickCounter(int which);      // diagnostics: 0 queries, 1 invalid walkers, 2 draws in a query, 3 keyed,
                                         // 4 requests, 5 walker table full, 6 draws outside a keyed query

// Site A' (native version 4): the order log. One record per phase-0 execution of a decision graph while switch bit 1
// or 2 is on: the driver key (spare seed, walker index), the walker's execution ordinal, the graph's hash, and the
// file positions of the first 16 nodes in the order they ran. kOrderWords words per record, the last the commit word
// (seq + 1 once whole).
constexpr int kOrderWords = 10;
constexpr uint32_t kOrderCapacity = 16384;  // a power of two

int __cdecl OrderCount();
int __cdecl OrderWord(int seq, int word);
int __cdecl OrderCounter(int which);  // see OrderCounterId in sacs_patch.cpp

// Item 3 (native version 5): the keyed Random-node draw's diagnostics.
int __cdecl RandomCounter(int which);  // see RandomCounterId in sacs_patch.cpp

// Item 3's input log (native version 6): one record per Random-node evaluate while switch bit 4 is on, whatever bit
// 3: the spare seed, walker index, flags and draw count, the graph ID, the node key with the outcome and the fallback
// reason, the boundary count, the evaluation, the walker's request count, the last value drawn (float bits), and the
// history the evaluate found. kRandomWords words per record, the last the commit word (seq + 1 once whole).
constexpr int kRandomWords = 10;
constexpr uint32_t kRandomCapacity = 16384;  // a power of two

int __cdecl RandomLogCount();
int __cdecl RandomLogWord(int seq, int word);

// Option C (native version 13, walker_copy_logic.h): a driver's walker counters copied to a client's copy instead of
// both games starting them again. Errors: -1 this build's bytes differ, -2 exception, -3 the handle is no driver,
// -4 index out of range, -5 the carry's old handle was never resolved here, -6 the walker table is full.
// WalkerRead: snapshots walker `index` of the handle's driver (nothing created or reset); returns the walker count
// (> index), 0 past the last walker, or an error. WalkerWord: that snapshot's words (0 the driver's spare, 1 generation,
// 2 draws, 3 boundaries, 4 executions, 5 1 when SimLink had counts under that spare).
// WalkerWrite: the counts onto the copy's walker `index` under `spare`: 1 written, 2 kept until the walker exists,
// 3 kept until the driver carries that spare, or an error.
// WalkerCarry: after a re-resolve, the old copy driver's counts under `spare` onto the new one's walkers of the same
// index; returns the walkers carried, or an error.
// WalkerCounter: diagnostics (see WalkerCounterId in sacs_patch.cpp).
// Optional read-only request trace: WalkerRead(handle, -1) returns 0x19500000|walkerCount
// (older builds return -4). WalkerWord(1000+n) then reads its separate snapshot:
// driver error, synchronized, count; 18 words per actor: actor hash, walker pointer,
// current pointer/hash, next pointer/hash, destination hash, flags, mode, queued count,
// and the first four queued (flags, destination hash) pairs. No state is changed.
int __cdecl WalkerRead(int handle, int index);
int __cdecl WalkerWord(int word);
int __cdecl WalkerWrite(int handle, int index, int spare, int generation, int draws, int boundaries, int executions);
int __cdecl WalkerCarry(int oldHandle, int newHandle, int spare);
int __cdecl WalkerCounter(int which);

// sidecar.cpp's handle resolution notes every driver it finds, so a carry can name a handle that no longer resolves.
void NoteResolved(uint32_t handle, uintptr_t driver);
}  // namespace sacs
}  // namespace simlink
