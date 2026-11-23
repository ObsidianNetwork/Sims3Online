// the on-screen walk's byte replacement. onscreen.cpp writes it at load (on by default from native version
// 12; SIMLINK_ONSCREEN=0 turns it off); selftest.cpp (`simlink_selftest onscreen`) checks the row and the default.
// Nothing here reads game memory.
//
// Every route follower asks, each frame and when a route starts, whether its Sim is on screen:
// TestRouteFollowerOnScreen (RVA 0x554240, thiscall, one stack argument) builds a 3 m sphere around the Sim and asks
// every camera. The answer picks the locomotion: for a Sim, an animated body and brain with start and stop clips when
// on screen, a steady-speed body and brain with none when off screen. So
// each game walked a Sim by where its own camera looked, and the server's walk and its copies' walks split.
//
// The row makes the test answer true: every Sim walks the animated, on-screen way in every game, whatever the
// cameras look at. It is the state any Sim is already in while a camera sees it. The five callers read only al
// (test al, al; mov [esi + 0xc8], al; cmp al, [esi + 0xc8]). The row is a whole instruction window, so its check
// covers the instructions it replaces.
#pragma once

#include <cstddef>
#include <cstdint>

#include "chunk_reserve_logic.h"

namespace simlink
{
namespace onscreen
{
using chunkreserve::ByteReplace;

// 0x954240, TestRouteFollowerOnScreen's entry: push ebp / mov ebp, esp / and esp, 0xfffffff0 becomes
// mov al, 1 / ret 4 / nop. The function's own epilogue is ret 4 (0x95432a): the replacement pops the same argument.
constexpr unsigned char kOnScreenEntry[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};
constexpr unsigned char kOnScreenAlways[] = {0xB0, 0x01, 0xC2, 0x04, 0x00, 0x90};

constexpr ByteReplace kOnScreenReplaces[] = {
    {0x00954240, kOnScreenEntry, kOnScreenAlways, sizeof(kOnScreenEntry)},
};
constexpr size_t kOnScreenCount = sizeof(kOnScreenReplaces) / sizeof(kOnScreenReplaces[0]);

static_assert(sizeof(kOnScreenEntry) == sizeof(kOnScreenAlways), "the replacement has its original's length");

// On by default (native version 12): only SIMLINK_ONSCREEN=0 turns it off. `length` is what GetEnvironmentVariableW
// returned for the value: 0 when the variable is not set or empty, the size it needs when the buffer is too small.
inline bool OnScreenWanted(const wchar_t* value, unsigned long length)
{
    return !(length == 1 && value[0] == L'0');
}
}  // namespace onscreen
}  // namespace simlink
