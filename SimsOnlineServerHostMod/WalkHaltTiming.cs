// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // The copy's recovery from a stopped walk (the user, 2026-10-21: replay the stop at the server's offset into the walk;
    // docs/engine/boundary-recording.md, "A walk the game stops"). Where a Soft stop lands decides how the Sim ends: before
    // its first step it plays the start clip and rests 0.22 m on, after it the Sim walks on to its goal (two-hud). A copy
    // applies the stop when Rh reaches it, and the copies were past the server's point then (564 and 600 ms into their
    // walks against the server's 530): the one at 600 ms had stepped, walked to the goal, and was snapped back 0.44 m (1 of
    // 12 copies). So the server sends how far into
    // its walk the stop came, and the copy applies it at the same point of its own walk. That only helps a copy that is
    // behind when Rh arrives: walk.start-lead-ms starts every copied walk that much later (0 = off), and the counters say
    // how late Rh came (haltLate) and whether a stop still landed on the other side of the first step (phaseSplits).
    // Public: the presentation module (a second script assembly) uses it.
    public static class WalkHaltTiming
    {
        // In a replay line seq|Rh|sim|x|y|z|route|stopMs|mode|moved|leftBefore|leftAfter|sinceMs|sinceTicks: the stop's
        // offset from the walk's start (RouteRecorder.OnStart, the Rs line's time) in ms and in SimClock ticks.
        public const int SinceMsField = 12;
        public const int SinceTicksField = 13;
        // Under this the Sim had not stepped yet (a walk tick moves 1.7-6 cm).
        public const float BeforeStep = 0.01f;

        // The two trailing Rh fields (the server).
        public static string Fields(long sinceMs, long sinceTicks)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "|" + Math.Max(0L, sinceMs).ToString(c) + "|" + Math.Max(0L, sinceTicks).ToString(c);
        }

        // The stop's offset in ms (ticks false) or SimClock ticks (ticks true), or -1 when the line has none (an older
        // server) or it does not parse: the copy then keeps the distance rule.
        public static long Offset(string[] f, bool ticks)
        {
            int at = ticks ? SinceTicksField : SinceMsField;
            long value;
            if (f == null || f.Length <= at || !long.TryParse(f[at], NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return -1L;
            }

            return value;
        }

        // Whether the copy, elapsed into its own walk (-1: its walk has not begun), has reached the server's offset.
        public static bool Due(long copyElapsed, long serverOffset)
        {
            return serverOffset < 0L || (copyElapsed >= 0L && copyElapsed >= serverOffset);
        }

        // How late the stop reached the copy against the server's point: above 0 the copy was past it, below 0 it was
        // behind (it holds the stop), and a copy whose walk had not begun counts as at its start.
        public static long Late(long copyElapsed, long serverOffset)
        {
            return Math.Max(0L, copyElapsed) - serverOffset;
        }

        // The stop landed on the other side of the first step on the copy: the server's Sim had not stepped, the copy had.
        public static bool PhaseSplit(float serverMoved, float copyMoved)
        {
            return serverMoved < BeforeStep && copyMoved >= BeforeStep;
        }

        // How much longer a copy's walk start waits for the lead, sinceReceived ms after its Rs reached this client.
        public static long LeadLeft(long sinceReceived, long leadMs)
        {
            return leadMs <= 0L ? 0L : Math.Max(0L, leadMs - Math.Max(0L, sinceReceived));
        }
    }
}
