using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // site A' (docs/engine/sacs-native-picks.md, "Site A': design"; SimLink/src/sacs_patch.cpp): the decision-graph
    // executor runs a state's pending nodes in file order instead of heap-address order. SimLink installs the patch with
    // tier 3 at load; it changes nothing until switch bit 1 is on. The tunable sacs.node-order sets the bits (both games;
    // SacsPicks.Sync pushes the whole mask): 0 off, 1 log only (bit 2), 2 file order and log (bits 1 and 2). This class
    // reads SimLink's order log (one record per phase-0 execution) for the controller: SACS_RECORDER orders <since> [max],
    // paired across the two games by scripts/sacs_picks.py. Every internal call is behind SimLinkShadow's wrappers and
    // OrderReady.
    internal static class SacsOrder
    {
        internal const string Tunable = "sacs.node-order";
        private const int Words = 10;
        private const int Capacity = 16384;
        // 1 (log only) by default from 2026-10-21: the level Session I's arms ran (the user chose their
        // configuration). File order (2) was not in those arms and stays opt-in.
        private static readonly CachedTunable sLevel = new CachedTunable(Tunable, 1f);
        private static string sLastError = "-";

        internal static int Level
        {
            get { return (int)sLevel.Value; }
        }

        // The switch bits for the level: 2 = file order, 4 = log.
        internal static int Bits
        {
            get
            {
                int level = Level;
                return level >= 2 ? 6 : level == 1 ? 4 : 0;
            }
        }

        internal static string Describe()
        {
            string level = Level.ToString(CultureInfo.InvariantCulture);
            if (!SimLinkShadow.OrderReady)
            {
                return "order=" + level + ",ready=0";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                string counters = string.Empty;
                string[] names = { "picks", "single", "applied", "differs", "nokey", "stale", "mixed", "duplicate", "badset",
                    "exception", "graphs", "nodes", "loadfail", "full", "logged", "nowalker", "midexec" };
                for (int i = 0; i < names.Length; i++)
                {
                    counters += "," + names[i] + "=" + SimLinkShadow.OrderCounter(i).ToString(c);
                }

                return "order=" + level + ",ready=1,count=" + SimLinkShadow.OrderCount().ToString(c) + counters + ",error=" + sLastError;
            }
            catch (Exception ex)
            {
                sLastError = ex.GetType().Name;
                return "order=error:" + sLastError;
            }
        }

        // Up to max whole records from seq since, within maxChars. Header "orders=<count>;start=<seq>;next=<seq>;lost=<n>",
        // then one row per execution: spare|walker|flags|steps|ordinal|graph|keys|differs|applied. spare is the driver's
        // spare seed (the same on both games once tier 1 copied it), walker its index on the driver, flags 1 partial
        // (a step without a position), 2 mixed graphs, 4 a step with two or more pending, 8 the file order used; graph
        // in hex; keys the file positions of the first 16 nodes in the order they ran, one hex byte each (00 = unknown);
        // differs = steps where the file-order pick was not the game's own, applied = steps that ran the file order.
        internal static string Page(int since, int max, int maxChars)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (!SimLinkShadow.OrderReady)
            {
                return "orders=-1;start=0;next=0;lost=0";
            }

            int count = SimLinkShadow.OrderCount();
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
                if ((uint)SimLinkShadow.OrderWord(seq, Words - 1) != (uint)(seq + 1))
                {
                    break; // not complete yet (or overwritten): the next page starts here
                }

                for (int i = 0; i < Words - 1; i++)
                {
                    w[i] = (uint)SimLinkShadow.OrderWord(seq, i);
                }

                if ((uint)SimLinkShadow.OrderWord(seq, Words - 1) != (uint)(seq + 1))
                {
                    break;
                }

                uint steps = w[1] >> 16;
                string keys = string.Empty;
                for (int k = 0; k < 16 && k < steps; k++)
                {
                    keys += ((w[4 + k / 4] >> (8 * (k % 4))) & 0xFF).ToString("X2", c);
                }

                string row = w[0].ToString(c) + "|" + (w[1] & 0xFF).ToString(c) + "|" + ((w[1] >> 8) & 0xFF).ToString(c) + "|" + steps.ToString(c)
                    + "|" + w[2].ToString(c) + "|" + w[3].ToString("X8", c) + "|" + keys + "|" + (w[8] & 0xFFFF).ToString(c) + "|" + (w[8] >> 16).ToString(c);
                rows.Add(row);
                chars += row.Length + 1;
                seq++;
            }

            return "orders=" + count.ToString(c) + ";start=" + start.ToString(c) + ";next=" + seq.ToString(c) + ";lost=" + lost.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }
    }
}
