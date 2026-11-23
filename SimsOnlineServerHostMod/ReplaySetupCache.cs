// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System.Collections.Generic;

namespace SimsOnlineServerHost
{
    // what a client's replay driver was set up with, per server driver: its seeds (AS|) and its actors and
    // parameters (S|, P|, Ps|, Pn|, Ph|), the latest line per key as the server's KeepGap keys them, in the order last
    // set. When the client's handle goes stale (kProxyInvalid), ServerHostMain acquires a new driver from the acquire
    // line alone; these lines go with it, the seeds first (before anything draws), before the request that failed.
    // Lines are the client's replay lines: seq|kind|serverDriver|...
    // Option C's AW| (walker counters) is left out on purpose: replayed after the AS| line it would overwrite the counts
    // SimLink carries over from the old copy driver (SacsWalkerCopy.Carry) with counts stale by every request since.
    // The server keeps one too (SacsRecorder.sParams, item 4): each driver's parameter lines with seq 0, for DriverHead.
    internal sealed class ReplaySetupCache
    {
        internal const int Capacity = 64; // per driver; the oldest setting goes first, the seeds stay

        private readonly Dictionary<uint, List<KeyValuePair<string, string>>> mLines =
            new Dictionary<uint, List<KeyValuePair<string, string>>>();

        // The cache key of a replay line (f = its fields), or null when the line does not set the driver up.
        internal static string KeyOf(string kind, string[] f)
        {
            if (kind == "AS")
            {
                return "AS";
            }

            if ((kind == "S" || kind == "P" || kind == "Ps") && f.Length > 3)
            {
                return kind + "|" + f[3];
            }

            if ((kind == "Pn" || kind == "Ph") && f.Length > 4)
            {
                return kind + "|" + f[3] + "|" + f[4];
            }

            return null;
        }

        // A line applied to the driver: kept when it sets the driver up.
        internal void Keep(uint serverDriver, string kind, string[] f, string line)
        {
            string key = KeyOf(kind, f);
            if (key == null)
            {
                return;
            }

            List<KeyValuePair<string, string>> setup;
            if (!mLines.TryGetValue(serverDriver, out setup))
            {
                setup = new List<KeyValuePair<string, string>>();
                mLines[serverDriver] = setup;
            }

            for (int i = 0; i < setup.Count; i++)
            {
                if (setup[i].Key == key)
                {
                    setup.RemoveAt(i);
                    break;
                }
            }

            if (setup.Count >= Capacity)
            {
                setup.RemoveAt(setup[0].Key == "AS" ? 1 : 0);
            }

            setup.Insert(key == "AS" ? 0 : setup.Count, new KeyValuePair<string, string>(key, line));
        }

        // The lines to replay onto a new driver, seeds first, then the settings in the order last set.
        internal List<string> Lines(uint serverDriver)
        {
            List<string> lines = new List<string>();
            List<KeyValuePair<string, string>> setup;
            if (mLines.TryGetValue(serverDriver, out setup))
            {
                for (int i = 0; i < setup.Count; i++)
                {
                    lines.Add(setup[i].Value);
                }
            }

            return lines;
        }

        internal void Forget(uint serverDriver)
        {
            mLines.Remove(serverDriver);
        }

        internal void Clear()
        {
            mLines.Clear();
        }

        internal int Count
        {
            get { return mLines.Count; }
        }
    }
}
