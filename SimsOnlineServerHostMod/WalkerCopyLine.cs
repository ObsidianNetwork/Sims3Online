// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // This line carries a server driver's walker counters to a client's copy. Its format and transfer rules
    // live apart from SacsWalkerCopy so the tests can check them.
    //   AW|driver|spare|g:d:b:e|g:d:b:e|...
    // One field per walker in the driver's walker order (the index the keys use): requests (tier 3's generation), draws
    // since the last request, requests and EnterStates (item 3's boundaries), and site A' executions. spare is the
    // server driver's spare seed when they were read: SimLink zeroes a walker's counters when its driver's spare changes,
    // so the copy keeps them only under that spare (an AS| line carries it first).
    internal static class WalkerCopyLine
    {
        internal const string Kind = "AW";
        internal const int Counters = 4;
        internal const int MaxWalkers = 64; // SimLink's bound on a driver's walker list
        // Drivers read and rung per bridge tick: a late join introduces every driver at once, and the tick stays cheap.
        internal const int PerTick = 48;

        // Result buckets of SacsWalkerWrite / SacsWalkerCarry placements.
        internal const int Written = 0;
        internal const int Pending = 1;      // kept until the walker exists
        internal const int SpareDiffers = 2; // kept until the copy's driver carries the counts' spare
        internal const int Failed = 3;

        internal static string Format(uint driver, uint spare, IList<uint[]> walkers)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string line = Kind + "|" + driver.ToString(c) + "|" + spare.ToString(c);
            for (int i = 0; i < walkers.Count; i++)
            {
                uint[] w = walkers[i];
                line += "|" + w[0].ToString(c) + ":" + w[1].ToString(c) + ":" + w[2].ToString(c) + ":" + w[3].ToString(c);
            }

            return line;
        }

        // f = seq|AW|driver|spare|walker...: the walkers' counts in index order, or null when the line is malformed (no
        // walker, a field without four counts, a spare of 0, more walkers than SimLink keeps).
        internal static List<uint[]> Parse(string[] f, out uint driver, out uint spare)
        {
            driver = 0u;
            spare = 0u;
            CultureInfo c = CultureInfo.InvariantCulture;
            if (f == null || f.Length < 5 || f[1] != Kind || f.Length - 4 > MaxWalkers
                || !uint.TryParse(f[2], NumberStyles.None, c, out driver) || !uint.TryParse(f[3], NumberStyles.None, c, out spare)
                || spare == 0u)
            {
                return null;
            }

            List<uint[]> walkers = new List<uint[]>();
            for (int i = 4; i < f.Length; i++)
            {
                string[] parts = f[i].Split(':');
                if (parts.Length != Counters)
                {
                    return null;
                }

                uint[] counts = new uint[Counters];
                for (int k = 0; k < Counters; k++)
                {
                    if (!uint.TryParse(parts[k], NumberStyles.None, c, out counts[k]))
                    {
                        return null;
                    }
                }

                walkers.Add(counts);
            }

            return walkers;
        }

        // Server: whether an introduction (DriverHead, SnapshotDriver's last page, a late AS| line) marks the driver for an
        // AW| line. Only while the copy is on, never for a cosmetic layer, and only for a driver whose seeds went out:
        // without the spare the copy's counters would be zeroed again at once.
        internal static bool Due(bool on, bool cosmetic, string seedLine)
        {
            return on && !cosmetic && seedLine != null;
        }

        // A SacsWalkerWrite / placement result as a bucket.
        internal static int Bucket(int result)
        {
            return result == 1 ? Written : result == 2 ? Pending : result == 3 ? SpareDiffers : Failed;
        }
    }
}
