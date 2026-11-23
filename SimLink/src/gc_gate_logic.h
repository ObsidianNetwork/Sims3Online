// the GC rate gate's decision (gc_gate.cpp), kept free of Windows and game memory so the self-test
// (simlink_selftest gcgate) checks the same code the gate runs.
//
// Every frame the gate reads GC_gc_no (collections finished) and GC_mark_state (not 0 while a collection is in
// progress). A change of GC_gc_no restarts the interval clock, whoever ran the collection (the frame call, or an
// allocation that collected). With the switch bit off the call passes through. With it on, the frame runs a whole
// collection when one is in progress or the interval has passed since the last one, and no collection otherwise.
#pragma once

#include <cstdint>

namespace simlink
{
namespace gc
{
enum Decision : int
{
    DecisionPass = 0,   // switch bit off: the game's own call with its own stop function
    DecisionAdmit = 1,  // run the collection to its end (the never-stop function)
    DecisionSkip = 2,   // no collection work this frame
};

struct GateMemory
{
    uint32_t lastGcNo;    // GC_gc_no as last read
    uint64_t lastDoneUs;  // when GC_gc_no last changed, or the last admitted call ended
    bool seen;            // lastGcNo holds a reading
    bool admittedOnce;    // an admitted call has run since the bit came on (heldAtEntry counts from then)
};

struct GateInput
{
    bool bitOn;
    uint32_t gcNo;       // GC_gc_no (0x011F2F90)
    uint32_t markState;  // GC_mark_state (0x011F3078)
    uint64_t nowUs;
    uint32_t intervalMs;
};

// One frame's decision. heldAtEntry: the bit is on, an admitted call has run since it came on, and the frame still
// found a collection in progress. Every admitted call runs to the end, so this should never happen; a count above 0
// means something else starts collections the gate does not finish.
inline Decision Decide(GateMemory& m, const GateInput& in, bool& heldAtEntry)
{
    if (!m.seen || in.gcNo != m.lastGcNo)
    {
        m.lastGcNo = in.gcNo;
        m.lastDoneUs = in.nowUs;
        m.seen = true;
    }

    heldAtEntry = in.bitOn && m.admittedOnce && in.markState != 0;
    if (!in.bitOn)
    {
        m.admittedOnce = false;  // the game's own incremental steps resume; a later bit-on starts afresh
        return DecisionPass;
    }

    if (in.markState != 0 || in.nowUs - m.lastDoneUs >= static_cast<uint64_t>(in.intervalMs) * 1000u)
    {
        return DecisionAdmit;
    }

    return DecisionSkip;
}

// After an admitted call: the collection it ran (or finished) is the last one, ended at endUs.
inline void Admitted(GateMemory& m, uint32_t gcNo, uint64_t endUs)
{
    m.lastGcNo = gcNo;
    m.lastDoneUs = endUs;
    m.seen = true;
    m.admittedOnce = true;
}
}  // namespace gc
}  // namespace simlink
