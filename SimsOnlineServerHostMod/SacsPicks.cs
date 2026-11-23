using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // tier 3 (SimLink/src/sacs_patch.cpp): keyed path picks. SimLink
    // installs the code patches at load, byte-checked against this TS3W.exe build; they change nothing until switch bit 0
    // is on. This class sets the switches from the tunable sacs.keyed-picks (on both games; Tunables.Set calls Sync) and
    // reads SimLink's pick log for the controller: SACS_RECORDER picks <since> [max], paired across the two games by
    // scripts/sacs_picks.py. Every internal call is behind SimLinkShadow's NoInlining wrappers and PicksReady.
    internal static class SacsPicks
    {
        internal const string Tunable = "sacs.keyed-picks";
        private const int Words = 10;
        private const int Capacity = 16384;
        // On by default from 2026-10-21 (the user, after Session I's arms); sacs.keyed-picks=0 turns it off.
        private static readonly CachedTunable sOn = new CachedTunable(Tunable, 1f);
        private static string sLastError = "-";

        internal static bool Wanted
        {
            get { return sOn.Value > 0f; }
        }

        // Pushes the tunable to SimLink's switch mask. Harmless before SimLink registers (it waits for PicksReady).
        internal static void Sync()
        {
            if (!SimLinkShadow.PicksReady)
            {
                return;
            }

            try
            {
                // bits 1-2: site A' (sacs.node-order); bits 3-4: item 3 (sacs.keyed-random, sacs.random-log);
                // bit 5: GC rate gate (gc.gate, server only)
                SimLinkShadow.SetSwitches((Wanted ? 1 : 0) | SacsOrder.Bits | SacsRandom.Bits | SacsGc.Bits);
                SacsGc.Sync();
            }
            catch (Exception ex)
            {
                sLastError = ex.GetType().Name;
            }
        }

        internal static string Describe()
        {
            if (!SimLinkShadow.PicksReady)
            {
                return "picks=" + (Wanted ? "on" : "off") + ",ready=0";
            }

            try
            {
                Sync(); // a tunable set before SimLink registered
                CultureInfo c = CultureInfo.InvariantCulture;
                int state = SimLinkShadow.PatchState();
                string counters = string.Empty;
                string[] names = { "queries", "invalid", "draws", "keyed", "requests", "full", "outside" };
                for (int i = 0; i < names.Length; i++)
                {
                    counters += "," + names[i] + "=" + SimLinkShadow.PickCounter(i).ToString(c);
                }

                return "picks=" + (Wanted ? "on" : "off") + ",ready=1,patch=" + (state & 0xFF).ToString(c) + "/" + (state >> 8).ToString(c)
                    + ",switches=" + SimLinkShadow.SetSwitches(-1).ToString(c) + ",count=" + SimLinkShadow.PickCount().ToString(c) + counters
                    + ",error=" + sLastError;
            }
            catch (Exception ex)
            {
                sLastError = ex.GetType().Name;
                return "picks=error:" + sLastError;
            }
        }

        // Up to max whole records from seq since, within maxChars. Header "picks=<count>;start=<seq>;next=<seq>;lost=<n>",
        // then one row per record from start, in order: driverKey|walker|site|mode|keyed|generation|draws|n|chosen|native|
        // from|to|hop (driverKey is the driver's spare seed, the same on both games once tier 1 copied it; hashes in hex).
        internal static string Page(int since, int max, int maxChars)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (!SimLinkShadow.PicksReady)
            {
                return "picks=-1;start=0;next=0;lost=0";
            }

            int count = SimLinkShadow.PickCount();
            int seq = Math.Max(0, since);
            int lost = 0;
            if (count - seq > Capacity)
            {
                lost = count - Capacity - seq;
                seq = count - Capacity;
            }

            int start = seq;
            List<string> rows = new List<string>();
            int chars = 0;
            uint[] w = new uint[Words];
            while (seq < count && rows.Count < max && chars < maxChars)
            {
                if ((uint)SimLinkShadow.PickWord(seq, Words - 1) != (uint)(seq + 1))
                {
                    break; // not complete yet (or overwritten): the next page starts here
                }

                for (int i = 0; i < Words - 1; i++)
                {
                    w[i] = (uint)SimLinkShadow.PickWord(seq, i);
                }

                if ((uint)SimLinkShadow.PickWord(seq, Words - 1) != (uint)(seq + 1))
                {
                    break;
                }

                string row = w[0].ToString(c) + "|" + (w[1] & 0xFF).ToString(c) + "|" + ((w[1] >> 8) & 0xFF).ToString(c)
                    + "|" + ((w[1] >> 16) & 0xFF).ToString(c) + "|" + (w[1] >> 24).ToString(c) + "|" + w[2].ToString(c) + "|" + w[3].ToString(c)
                    + "|" + (w[4] & 0xFFFF).ToString(c) + "|" + (w[4] >> 16).ToString(c) + "|" + w[5].ToString(c) + "|" + w[6].ToString("X8", c)
                    + "|" + w[7].ToString("X8", c) + "|" + w[8].ToString("X8", c);
                rows.Add(row);
                chars += row.Length + 1;
                seq++;
            }

            return "picks=" + count.ToString(c) + ";start=" + start.ToString(c) + ";next=" + seq.ToString(c) + ";lost=" + lost.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }
    }
}
