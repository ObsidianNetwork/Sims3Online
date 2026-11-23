using System;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // The explicit-free ring (SimLink/src/free_ring.cpp, native version 8; layout 2 from native version 9). In a
    // game started with SIMLINK_FREERING=1 (the server controller's --server-env; opt-in, so other runs keep GC_free as
    // it is), SimLink hooks GC_free's entry at load and keeps the last explicit frees (131072 from native version 9) in
    // SimLink_FreeRing, for a crash report to look the stale block up in (scripts/crash_catch.py analyze reads it from a
    // full dump; the layout is free_ring_logic.h). The mod only reports it: no tunable, no switch bit.
    //
    // Status field (SACS_RECORDER status): freeRing=on|off,installed=N,site=N,addr=0xXXXXXXXX,count=N,perSec=N,
    // capacity=N, then offScript=N,scriptTid=N,installTid=N,layout=N (native version 9) or offMain=N (8). installed is
    // SimLink's install state (1 installed; 0 not attempted, so no SIMLINK_FREERING=1; 2 kill switch, 3 another build, 4
    // a byte mismatch at check `site`, 5 protect or the compare-exchange failed, 6 exception); on means installed=1.
    // count is the frees recorded, perSec the frees over the last window of at least a second. offScript counts the
    // frees on another thread than the script thread (scriptTid: the thread of the first frame-release free, 0 until
    // one). offMain counted the frees off the installing thread, which is not the script thread, so it equals count.
    internal static class SacsFreeRing
    {
        internal static string Describe()
        {
            if (!SimLinkShadow.FreeRingReady)
            {
                return "freeRing=off,ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                int state = SimLinkShadow.FreeRing(3);
                int installed = state & 0xFF;
                return "freeRing=" + (installed == 1 ? "on" : "off") + ",installed=" + installed.ToString(c)
                    + ",site=" + (state >> 8).ToString(c) + ",addr=0x" + ((uint)SimLinkShadow.FreeRing(2)).ToString("X8", c)
                    + ",count=" + ((uint)SimLinkShadow.FreeRing(0)).ToString(c) + ",perSec=" + SimLinkShadow.FreeRing(1).ToString(c)
                    + ",capacity=" + SimLinkShadow.FreeRing(4).ToString(c) + Threads(c);
            }
            catch (Exception ex)
            {
                return "freeRing=error:" + ex.GetType().Name;
            }
        }

        // The thread fields: native version 9 (layout 2) names the script thread; version 8 has offMain only.
        private static string Threads(CultureInfo c)
        {
            if (!SimLinkShadow.FreeRingThreadsReady)
            {
                return ",offMain=" + SimLinkShadow.FreeRing(5).ToString(c);
            }

            return ",offScript=" + SimLinkShadow.FreeRing(5).ToString(c) + ",scriptTid=" + SimLinkShadow.FreeRing(6).ToString(c)
                + ",installTid=" + SimLinkShadow.FreeRing(7).ToString(c) + ",layout=" + SimLinkShadow.FreeRing(8).ToString(c);
        }
    }
}
