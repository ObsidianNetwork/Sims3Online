#pragma once

#include <windows.h>

namespace simlink
{
namespace sequenceguard
{
void Install(HMODULE self);
// 0: sacs::PatchState; 1: head checks; 2: sentinel exhaustion prevented.
int __cdecl Counter(int which);
}  // namespace sequenceguard
}  // namespace simlink
