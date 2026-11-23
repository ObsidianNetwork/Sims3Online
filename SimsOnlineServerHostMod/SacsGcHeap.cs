using System;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // The GC heap lever (SimLink/src/gc_heap.cpp, native version 14; on by default from 15). SimLink rewrites Boehm's
    // initial heap (40 MB) and growth step (8 MB) at load, before GC_init runs (gc_heap_logic.h): to 352 MB and 32 MB
    // when SIMLINK_GCHEAP is unset, to SIMLINK_GCHEAP=<initialMB>,<growthMB>, or not at all with SIMLINK_GCHEAP=0.
    // With SIMLINK_GCFORCED=1 (opt-in) it times the forced collections EA's GC_collect_or_expand runs instead of
    // growing the heap. The mod only reports them.
    //
    // Status field (SACS_RECORDER status): gcHeap=on|off,installed=N,site=N,sites=N,initialMB=N,growthMB=N,heapMB=N,
    // gcNo=N,initAtLoad=N,timer=N,timerSite=N,forced=N,forcedMs=N,forcedMaxMs=F,forcedLastMs=F,forced50=N,forced100=N,
    // commitMB=N,peakCommitMB=N.
    // - installed is the heap rows' install state: 1 installed (on); 0 not attempted (SIMLINK_GCHEAP=0, or unset before
    //   native version 15); 2 kill switch, 3 another build, 4 a byte mismatch at `site` (1-7 a pin, 8-11 a row), 5 a
    //   page that could not be made writable, 6 exception, 7 SIMLINK_GCHEAP malformed (site 2) or out of bounds (site
    //   3), 8 GC_init had already run. sites is the rows written: 4 when on. initialMB and growthMB are SIMLINK_GCHEAP
    //   as parsed (352 and 32 when unset from version 15).
    // - heapMB (GC_heapsize) and gcNo (GC_gc_no) read the game now, -1 when SimLink's pins did not match; initAtLoad is
    //   GC_is_initialized when SimLink loaded (0: the rows could take).
    // - timer is the forced timer's install state (same codes; 0: no SIMLINK_GCFORCED=1; timerSite 12 is the thunk);
    //   forced counts the forced collections, forcedMs their total, forcedMaxMs the longest since load, forced50 and
    //   forced100 those of 50 ms and 100 ms or more (counts an A/B can difference per arm).
    // - commitMB and peakCommitMB are the process's commit (PagefileUsage) now and at its peak.
    // Before native version 14: gcHeap=off,ready=0.
    internal static class SacsGcHeap
    {
        internal static string Describe()
        {
            if (!SimLinkShadow.GcHeapReady)
            {
                return "gcHeap=off,ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                int state = SimLinkShadow.GcHeap(0);
                int installed = state & 0xFF;
                int timer = SimLinkShadow.GcHeap(6);
                return "gcHeap=" + (installed == 1 ? "on" : "off") + ",installed=" + installed.ToString(c)
                    + ",site=" + (state >> 8).ToString(c) + ",sites=" + SimLinkShadow.GcHeap(1).ToString(c)
                    + ",initialMB=" + SimLinkShadow.GcHeap(2).ToString(c) + ",growthMB=" + SimLinkShadow.GcHeap(3).ToString(c)
                    + ",heapMB=" + SimLinkShadow.GcHeap(4).ToString(c) + ",gcNo=" + SimLinkShadow.GcHeap(5).ToString(c)
                    + ",initAtLoad=" + SimLinkShadow.GcHeap(15).ToString(c)
                    + ",timer=" + (timer & 0xFF).ToString(c) + ",timerSite=" + (timer >> 8).ToString(c)
                    + ",forced=" + SimLinkShadow.GcHeap(7).ToString(c) + ",forcedMs=" + SimLinkShadow.GcHeap(8).ToString(c)
                    + ",forcedMaxMs=" + Ms(SimLinkShadow.GcHeap(9)) + ",forcedLastMs=" + Ms(SimLinkShadow.GcHeap(10))
                    + ",forced50=" + SimLinkShadow.GcHeap(11).ToString(c) + ",forced100=" + SimLinkShadow.GcHeap(12).ToString(c)
                    + ",commitMB=" + SimLinkShadow.GcHeap(13).ToString(c) + ",peakCommitMB=" + SimLinkShadow.GcHeap(14).ToString(c);
            }
            catch (Exception ex)
            {
                return "gcHeap=error:" + ex.GetType().Name;
            }
        }

        private static string Ms(int microseconds)
        {
            return (microseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
