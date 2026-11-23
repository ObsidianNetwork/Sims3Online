using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // item 3 (docs/engine/sacs-native-picks.md, "Item 3: the keyed Random draw"; SimLink/src/sacs_patch.cpp):
    // a Random node's draw keyed by the driver's spare seed, the walker's index, the graph ID and node key (site A'),
    // the walker's boundary count, the node's evaluations since that boundary and the retry, so both games take the
    // same weighted outcome. SimLink installs the patch with tier 3 at load; it changes nothing until switch bit 3 is
    // on. The tunable sacs.keyed-random (both games, on by default from 2026-10-21 after Session I's arms;
    // SacsPicks.Sync pushes the whole mask) sets it.
    // Independent of sacs.keyed-picks and sacs.node-order. Every internal call is behind SimLinkShadow's wrappers and
    // RandomReady (native version 5).
    //
    // The input log (native version 6, RandomLogReady): with sacs.random-log on (switch bit 4, off by default, whatever
    // sacs.keyed-random), SimLink logs one record per Random-node evaluate: the key's fields, the outcome, the history
    // the evaluate found and the value drawn. It changes nothing in the game. SACS_RECORDER randoms <since> [max] pages
    // it for scripts/sacs_picks.py, which aligns the two games' records per driver key.
    internal static class SacsRandom
    {
        internal const string Tunable = "sacs.keyed-random";
        internal const string LogTunable = "sacs.random-log";
        private const int Words = 10;
        private const int Capacity = 16384;
        private static readonly CachedTunable sOn = new CachedTunable(Tunable, 1f);
        private static readonly CachedTunable sLog = new CachedTunable(LogTunable, 0f);
        private static string sLastError = "-";

        internal static bool Wanted
        {
            get { return sOn.Value > 0f; }
        }

        internal static bool LogWanted
        {
            get { return sLog.Value > 0f; }
        }

        // Switch bits 3 (keyed) and 4 (the input log); 0 on an older SimLink, which does not know them.
        internal static int Bits
        {
            get { return (Wanted && SimLinkShadow.RandomReady ? 8 : 0) | (LogWanted && SimLinkShadow.RandomLogReady ? 16 : 0); }
        }

        internal static string Describe()
        {
            string wanted = Wanted ? "1" : "0";
            if (!SimLinkShadow.RandomReady)
            {
                return "random=" + wanted + ",ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                string counters = string.Empty;
                string[] names = { "evaluations", "draws", "keyed", "retries", "resets", "nowalker", "nocontext", "nokey",
                    "nospare", "full", "exception", "outside", "enters", "logged" };
                int known = SimLinkShadow.RandomLogReady ? names.Length : names.Length - 1;
                for (int i = 0; i < known; i++)
                {
                    counters += "," + names[i] + "=" + SimLinkShadow.RandomCounter(i).ToString(c);
                }

                string log = SimLinkShadow.RandomLogReady
                    ? ",log=" + (LogWanted ? "1" : "0") + ",count=" + SimLinkShadow.RandomCount().ToString(c)
                    : ",log=-";
                return "random=" + wanted + ",ready=1" + log + counters + ",error=" + sLastError;
            }
            catch (Exception ex)
            {
                sLastError = ex.GetType().Name;
                return "random=error:" + sLastError;
            }
        }

        // Up to max whole records from seq since, within maxChars. Header "randoms=<count>;start=<seq>;next=<seq>;lost=<n>"
        // (count -1 before native version 6), then one row per evaluate:
        // spare|walker|flags|draws|graph|node|outcome|reason|boundary|evaluation|requests|value|history. spare is the
        // driver's spare seed and walker its index (the driver key); flags 1 keyed, 2 history reset, 4 a new boundary, 8
        // outcome unread, 16 sacs.keyed-random on; draws the values drawn (retries + 1; 0 on the Deterministic branch);
        // graph the A' graph ID (hex); node the A' node key; outcome the accepted outcome (255 unread); reason why a key
        // is incomplete (0 whole, 1 no walker, 2 context, 3 spare 0, 4 no node key, 5 table full, 6 exception);
        // boundary the walker's requests and EnterStates, evaluation this node's evaluations by the walker since the
        // boundary, requests the walker's requests alone (boundary - requests = EnterStates); value the last value drawn
        // and history the node's four history bytes as the evaluate found them, both hex.
        internal static string Page(int since, int max, int maxChars)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (!SimLinkShadow.RandomLogReady)
            {
                return "randoms=-1;start=0;next=0;lost=0";
            }

            int count = SimLinkShadow.RandomCount();
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
                if ((uint)SimLinkShadow.RandomWord(seq, Words - 1) != (uint)(seq + 1))
                {
                    break; // not complete yet (or overwritten): the next page starts here
                }

                for (int i = 0; i < Words - 1; i++)
                {
                    w[i] = (uint)SimLinkShadow.RandomWord(seq, i);
                }

                if ((uint)SimLinkShadow.RandomWord(seq, Words - 1) != (uint)(seq + 1))
                {
                    break;
                }

                string row = w[0].ToString(c) + "|" + (w[1] & 0xFF).ToString(c) + "|" + ((w[1] >> 8) & 0xFF).ToString(c)
                    + "|" + (w[1] >> 16).ToString(c) + "|" + w[2].ToString("X8", c) + "|" + (w[3] & 0xFFFF).ToString(c)
                    + "|" + ((w[3] >> 16) & 0xFF).ToString(c) + "|" + (w[3] >> 24).ToString(c) + "|" + w[4].ToString(c)
                    + "|" + w[5].ToString(c) + "|" + w[6].ToString(c) + "|" + w[7].ToString("X8", c) + "|" + w[8].ToString("X8", c);
                rows.Add(row);
                chars += row.Length + 1;
                seq++;
            }

            return "randoms=" + count.ToString(c) + ";start=" + start.ToString(c) + ";next=" + seq.ToString(c) + ";lost=" + lost.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }
    }
}
