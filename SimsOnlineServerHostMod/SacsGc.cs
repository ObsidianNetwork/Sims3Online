using System;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // GC rate gate (SimLink/src/gc_gate.cpp, native version 7). In a game started with SIMLINK_GCGATE=1 (the
    // server controller's --server-env; opt-in, so other runs keep the stock call path), SimLink retargets the frame
    // loop's one call into the Boehm collector at load; with switch bit 5 off the call passes through, the game's own
    // incremental steps.
    // The tunable gc.gate (off by default, set on the server game only: the bit is never set on a client) turns the
    // gate on: a frame then runs a whole collection when one is in progress or gc.gate-interval-ms (default 6000) has
    // passed since the last collection ended, and no collection work otherwise. SacsPicks.Sync pushes the whole switch
    // mask and calls Sync for the interval. Every internal call is behind SimLinkShadow's wrappers and GcGateReady.
    //
    // Status field (SACS_RECORDER status): gcGate=on|off,installed=N,site=N,bit=0|1,calls=N,admitted=N,skipped=N,
    // heldAtEntry=N,intervalMs=N,pauseMeanMs=F,pauseMaxMs=F,pauseLastMs=F. installed is SimLink's install state
    // (1 installed; 0 not attempted, so no SIMLINK_GCGATE=1; 2 kill switch, 3 another build, 4 a byte mismatch at check
    // `site`, 5 protect failed, 6 exception). calls counts every entry; admitted and skipped only frames with the bit on; heldAtEntry the entries,
    // after an admitted call since the bit came on, that found a collection in progress; the pauses cover admitted calls.
    internal static class SacsGc
    {
        internal const string Tunable = "gc.gate";
        internal const string IntervalTunable = "gc.gate-interval-ms";
        internal const int DefaultIntervalMs = 6000;
        internal const int Bit = 32;
        private static readonly CachedTunable sGate = new CachedTunable(Tunable, 0f);
        private static readonly CachedTunable sInterval = new CachedTunable(IntervalTunable, DefaultIntervalMs);
        private static string sLastError = "-";

        internal static bool Wanted
        {
            get { return sGate.Value > 0f && ServerHostMain.IsServerRole; }
        }

        // Switch bit 5; 0 on an older SimLink, which does not know it.
        internal static int Bits
        {
            get { return Wanted && SimLinkShadow.GcGateReady ? Bit : 0; }
        }

        internal static int IntervalMs
        {
            get
            {
                double ms = sInterval.Value;
                return ms <= 0 ? 0 : ms >= int.MaxValue ? int.MaxValue : (int)ms;
            }
        }

        // Pushes the interval to SimLink. Called from SacsPicks.Sync, inside its try.
        internal static void Sync()
        {
            if (!SimLinkShadow.GcGateReady)
            {
                return;
            }

            int ms = IntervalMs;
            if (SimLinkShadow.GcInterval(-1) != ms)
            {
                SimLinkShadow.GcInterval(ms);
            }
        }

        internal static string Describe()
        {
            string wanted = Wanted ? "on" : "off";
            if (!SimLinkShadow.GcGateReady)
            {
                return "gcGate=" + wanted + ",ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                int state = SimLinkShadow.GcCounter(7);
                int bit = (SimLinkShadow.SetSwitches(-1) & Bit) != 0 ? 1 : 0;
                return "gcGate=" + wanted + ",installed=" + (state & 0xFF).ToString(c) + ",site=" + (state >> 8).ToString(c)
                    + ",bit=" + bit.ToString(c) + ",calls=" + SimLinkShadow.GcCounter(0).ToString(c)
                    + ",admitted=" + SimLinkShadow.GcCounter(1).ToString(c) + ",skipped=" + SimLinkShadow.GcCounter(2).ToString(c)
                    + ",heldAtEntry=" + SimLinkShadow.GcCounter(3).ToString(c) + ",intervalMs=" + SimLinkShadow.GcInterval(-1).ToString(c)
                    + ",pauseMeanMs=" + Ms(SimLinkShadow.GcCounter(4)) + ",pauseMaxMs=" + Ms(SimLinkShadow.GcCounter(5))
                    + ",pauseLastMs=" + Ms(SimLinkShadow.GcCounter(6)) + ",error=" + sLastError;
            }
            catch (Exception ex)
            {
                sLastError = ex.GetType().Name;
                return "gcGate=error:" + sLastError;
            }
        }

        private static string Ms(int microseconds)
        {
            return (microseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
