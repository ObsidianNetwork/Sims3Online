using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // option C (docs/engine/sacs-native-picks.md, "Option C, copy instead of reset"; SimLink native version 13):
    // a client that meets a server driver mid-life copies the server's walker counters instead of counting from 0, so the
    // keys of tier 3, site A' and item 3 (spare seed plus per-walker counters) agree from there on. On with the seed copy
    // by default; sacs.walker-copy=0 on a game turns it off there.
    //   server: a driver introduced to a client (SacsRecorder.DriverHead, SnapshotDriver's last page, an AS| line sent at a
    //           state call for a driver that ran while the copy was off) is marked; the bridge tick reads its walkers'
    //           counters (SimLink creates and resets nothing for the read) and rings AW| (WalkerCopyLine), at most
    //           PerTick drivers a tick. Read on the tick, never on the dump path, like the copy-on resend.
    //   client: writes them onto its copy's walkers of the same index under the spare they were read with, or SimLink
    //           keeps them until it first meets that walker under that spare (the walker does not exist yet, or the AS|
    //           line has not landed). The line is not kept for replay (ReplaySetupCache.KeyOf leaves AW| out): after a
    //           re-resolve it is stale by the requests since, and the carry is fresher.
    //   re-resolve: when the client re-acquires a driver after kProxyInvalid and the cached AS| line puts the old copy's
    //           spare on the new driver, SimLink carries the old copy driver's counters onto it (SacsSeedCopy.Apply). No
    //           word goes to or from the server; a copy that was aligned stays aligned.
    // Not copied: item 3's per-node evaluations and the game's no-repeat history, so keyed Random evaluates on a copied
    // walker are the copy's own until its next request or EnterState (the residual; SimLink counts the windows and the
    // evaluates and draws in them, and flags those evaluates in the input log).
    internal static class SacsWalkerCopy
    {
        internal const string Tunable = "sacs.walker-copy";
        private static readonly CachedTunable sOn = new CachedTunable(Tunable, 1f);
        // Server
        internal static long Marked;
        internal static long Lines;
        internal static long Walkers;
        internal static long Empty;      // no walker yet: nothing to copy (both games start them at 0)
        internal static long ReadErrors;
        internal static long Respared;   // the spare changed between two walkers' reads: sent at its next introduction
        // Client
        internal static long Applied;
        internal static long Malformed;
        internal static long Skipped;
        internal static readonly long[] Placed = new long[4]; // WalkerCopyLine buckets
        internal static long Carries;
        internal static long Carried;
        internal static long CarryFailed;
        private static readonly Dictionary<uint, string> sOutcome = new Dictionary<uint, string>(); // client: per server driver
        private static readonly string[] sRecent = new string[8];
        private static int sRecentNext;

        // Managed state only: safe whether or not SimLink is registered.
        internal static bool Wanted
        {
            get { return sOn.Value > 0f; }
        }

        // Both games: the seed copy on (the spare tag comes with it) and SimLink with the walker calls.
        internal static bool On
        {
            get { return Wanted && SacsSeedCopy.On && SimLinkShadow.WalkersReady; }
        }

        // Server: the AW| line for this driver, or null (no walkers yet, a spare of 0, or a read that failed).
        internal static string ReadLine(uint driverHandle)
        {
            List<uint[]> walkers = new List<uint[]>();
            uint spare = 0u;
            for (int i = 0; i < WalkerCopyLine.MaxWalkers; i++)
            {
                int count = SimLinkShadow.WalkerRead(driverHandle, i);
                if (count < 0)
                {
                    ReadErrors++;
                    Note("server " + U(driverHandle) + " read=" + count.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                if (count == 0)
                {
                    break;
                }

                uint walkerSpare = (uint)SimLinkShadow.WalkerWord(0);
                if (i > 0 && walkerSpare != spare)
                {
                    Respared++;
                    return null;
                }

                spare = walkerSpare;
                walkers.Add(new uint[] { (uint)SimLinkShadow.WalkerWord(1), (uint)SimLinkShadow.WalkerWord(2),
                    (uint)SimLinkShadow.WalkerWord(3), (uint)SimLinkShadow.WalkerWord(4) });
                if (i + 1 >= count)
                {
                    break;
                }
            }

            if (walkers.Count == 0 || spare == 0u)
            {
                Empty++;
                return null;
            }

            Lines++;
            Walkers += walkers.Count;
            Note("server " + U(driverHandle) + " spare=" + U(spare) + " walkers=" + walkers.Count.ToString(CultureInfo.InvariantCulture)
                + " g0=" + U(walkers[0][0]) + " b0=" + U(walkers[0][2])); // no '|' in a status reply
            return WalkerCopyLine.Format(driverHandle, spare, walkers);
        }

        // Client: f = seq|AW|serverDriver|spare|walker..., onto the copy's driver.
        internal static void Apply(uint serverDriver, uint clientDriver, string[] f)
        {
            if (!On)
            {
                Skipped++;
                return;
            }

            uint driver;
            uint spare;
            List<uint[]> walkers = WalkerCopyLine.Parse(f, out driver, out spare);
            if (walkers == null)
            {
                Malformed++;
                return;
            }

            int[] buckets = new int[4];
            for (int i = 0; i < walkers.Count; i++)
            {
                int bucket = WalkerCopyLine.Bucket(SimLinkShadow.WalkerWrite(clientDriver, i, unchecked((int)spare), walkers[i]));
                buckets[bucket]++;
                Placed[bucket]++;
            }

            Applied++;
            if (sOutcome.Count > 4096)
            {
                sOutcome.Clear();
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            string outcome = buckets[WalkerCopyLine.Written].ToString(c) + "/" + buckets[WalkerCopyLine.Pending].ToString(c) + "/"
                + buckets[WalkerCopyLine.SpareDiffers].ToString(c) + "/" + buckets[WalkerCopyLine.Failed].ToString(c);
            sOutcome[serverDriver] = outcome;
            Note("client " + U(serverDriver) + "->" + U(clientDriver) + " " + outcome);
        }

        // Client, after a re-resolve's AS| line put the old copy's spare on the new copy driver (SacsSeedCopy.Apply).
        internal static void Carry(uint serverDriver, uint oldDriver, uint newDriver, int spare)
        {
            if (!On)
            {
                return;
            }

            int carried = SimLinkShadow.WalkerCarry(oldDriver, newDriver, spare);
            if (carried < 0)
            {
                CarryFailed++;
            }
            else
            {
                Carries++;
                Carried += carried;
            }

            Note("carry " + U(serverDriver) + " " + U(oldDriver) + "->" + U(newDriver) + "=" + carried.ToString(CultureInfo.InvariantCulture));
        }

        // Client: "handle:written/pending/spareDiffers/failed" of the last AW| line per server driver asked about
        // (comma-separated handles), "-" for a driver that got none.
        internal static string OutcomeReport(string handles)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> rows = new List<string>();
            foreach (string part in (handles ?? string.Empty).Split(','))
            {
                uint handle;
                if (!uint.TryParse(part, NumberStyles.Integer, c, out handle))
                {
                    continue;
                }

                string outcome;
                rows.Add(U(handle) + ":" + (sOutcome.TryGetValue(handle, out outcome) ? outcome : "-"));
            }

            return "walkerOutcomes=" + sOutcome.Count.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }

        internal static string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string recent = string.Empty;
            for (int i = 0; i < sRecent.Length; i++)
            {
                string entry = sRecent[(sRecentNext + i) % sRecent.Length];
                if (entry != null)
                {
                    recent += (recent.Length == 0 ? string.Empty : ",") + entry;
                }
            }

            return "walkerCopy=" + (Wanted ? "on" : "off") + ",ready=" + (SimLinkShadow.WalkersReady ? "1" : "0")
                + ",awMarked=" + Marked.ToString(c) + ",awLines=" + Lines.ToString(c) + ",awWalkers=" + Walkers.ToString(c)
                + ",awEmpty=" + Empty.ToString(c) + ",awReadErrors=" + ReadErrors.ToString(c) + ",awRespared=" + Respared.ToString(c)
                + ",awApplied=" + Applied.ToString(c) + ",awWritten=" + Placed[WalkerCopyLine.Written].ToString(c)
                + ",awPending=" + Placed[WalkerCopyLine.Pending].ToString(c) + ",awSpareDiffers=" + Placed[WalkerCopyLine.SpareDiffers].ToString(c)
                + ",awFailed=" + Placed[WalkerCopyLine.Failed].ToString(c) + ",awMalformed=" + Malformed.ToString(c) + ",awSkipped=" + Skipped.ToString(c)
                + ",awCarries=" + Carries.ToString(c) + ",awCarried=" + Carried.ToString(c) + ",awCarryFailed=" + CarryFailed.ToString(c)
                + ",native=[" + NativeCounters() + "],recent=[" + recent + "]";
        }

        // SimLink's counters (sacs_patch.cpp WalkerCounterId), or "-" without the walker calls.
        private static readonly string[] sNativeNames = new string[]
        {
            "nwReads", "nwWritten", "nwStaged", "nwSpareDiffers", "nwNoDriver", "nwPendingStaged", "nwPendingReplaced",
            "nwPendingTaken", "nwPendingEvicted", "nwCarries", "nwCarried", "nwCarryNoOld", "nwWindows", "nwWindowsClosed",
            "nwResidualEvals", "nwResidualDraws", "nwExceptions", "nwTableFull",
        };

        private static string NativeCounters()
        {
            if (!SimLinkShadow.WalkersReady)
            {
                return "-";
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                string text = string.Empty;
                for (int i = 0; i < sNativeNames.Length; i++)
                {
                    text += (i == 0 ? string.Empty : ",") + sNativeNames[i] + ":" + SimLinkShadow.WalkerCounter(i).ToString(c);
                }

                return text;
            }
            catch (Exception)
            {
                return "error";
            }
        }

        private static void Note(string entry)
        {
            sRecent[sRecentNext] = entry.Length > 120 ? entry.Substring(0, 120) : entry;
            sRecentNext = (sRecentNext + 1) % sRecent.Length;
        }

        private static string U(uint value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
