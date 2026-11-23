using System;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // The chunk reserve (SimLink/src/chunk_reserve.cpp, native version 10). SimLink rewrites the interpreter's
    // two frame fit tests and its new-chunk size at load, so that no frame ends at a stack chunk's end, where the
    // chunk's typed GC descriptor is (chunk_reserve_logic.h). From native version 11 it does so in every game unless
    // SIMLINK_CHUNKRESERVE=0; version 10 did only with SIMLINK_CHUNKRESERVE=1. The mod only reports it.
    //
    // Status field (SACS_RECORDER status): chunkReserve=on|off,installed=N,site=N,sites=N. installed is SimLink's
    // install state (1 installed; 0 not attempted: SIMLINK_CHUNKRESERVE=0, or on version 10 no SIMLINK_CHUNKRESERVE=1;
    // 2 kill switch, 3 another build, 4 a byte mismatch at row `site`, 5 a page that could not be made writable at row
    // `site`, with the rows already written put back, 6 exception); on means installed=1. sites is the rows written: 3
    // when on, 0 otherwise.
    internal static class SacsChunkReserve
    {
        internal static string Describe()
        {
            if (!SimLinkShadow.ChunkReserveReady)
            {
                return "chunkReserve=off,ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                int state = SimLinkShadow.ChunkReserve(0);
                int installed = state & 0xFF;
                return "chunkReserve=" + (installed == 1 ? "on" : "off") + ",installed=" + installed.ToString(c)
                    + ",site=" + (state >> 8).ToString(c) + ",sites=" + SimLinkShadow.ChunkReserve(1).ToString(c);
            }
            catch (Exception ex)
            {
                return "chunkReserve=error:" + ex.GetType().Name;
            }
        }
    }
}
