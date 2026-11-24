#pragma once

#include <cstdint>

namespace simlink
{
namespace sequenceguard
{
// A sequence-list sentinel has links, not a clip payload. Recheck after every
// callback/pop, not just before searching for the expected stop node.
inline bool HeadReadable(uintptr_t head, uintptr_t sentinel)
{
    return head != sentinel;
}

// Cleanroom transition across the patched loop-head site: verify head readability
// and compute the node subobject pointer passed forward to g_resume.
inline bool AdvanceReadableHead(uintptr_t head, uintptr_t sentinel, uintptr_t* outNext)
{
    if (outNext == nullptr || !HeadReadable(head, sentinel))
    {
        return false;
    }

    *outNext = head + 8u;
    return true;
}
}  // namespace sequenceguard
}  // namespace simlink
