// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    // part A: who uses an object, on a client as on the server. A client's pie menu runs the client's own tests on
    // its own copies, and many tests read whether the object or one of its parts is in use: GameObject.InUse is a Sim
    // parent or mActorsUsingMe (GameObject.cs:2220; AddToUseList / RemoveFromUseList fill it), and a seat or bed part is
    // used while PartData.mContainedSim is set (PartData.CanBeUsedBy, which Nap's seat search reads: the loveseat a Sim
    // reads on still offered Nap to the other player, sweep 2026-10-21). A copy runs no interactions, so its lists stay
    // empty. The server sends an object's users when they change and now and then again:
    //   Us|object|sim,sim,...|area:sim,area:sim,...   (ids ascending; parts by PartArea ascending; "-" = none)
    // and the client sets its copy's lists to its copies of those Sims (Presentation/Uses.cs), touching only the entries
    // it set itself. Public: the presentation module, a separate assembly, reads the lines with it.
    public static class ObjectUse
    {
        public const string Kind = "Us";

        // users: the Sims in the use list; areas and partSims run parallel: each used part's PartArea and its Sim.
        public static string Line(ulong obj, List<ulong> users, List<uint> areas, List<ulong> partSims)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<ulong> sorted = users == null ? new List<ulong>() : new List<ulong>(users);
            sorted.Sort();
            StringBuilder sb = new StringBuilder(Kind).Append('|').Append(obj.ToString(c)).Append('|');
            int written = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i] == 0uL || (i > 0 && sorted[i] == sorted[i - 1]))
                {
                    continue;
                }

                sb.Append(written++ > 0 ? "," : "").Append(sorted[i].ToString(c));
            }

            sb.Append(written == 0 ? "-|" : "|");
            List<int> order = new List<int>();
            for (int i = 0; areas != null && partSims != null && i < areas.Count && i < partSims.Count; i++)
            {
                if (partSims[i] != 0uL)
                {
                    order.Add(i);
                }
            }

            order.Sort(delegate(int a, int b) { return areas[a] != areas[b] ? areas[a].CompareTo(areas[b]) : a.CompareTo(b); });
            written = 0;
            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                if (k > 0 && areas[i] == areas[order[k - 1]])
                {
                    continue;
                }

                sb.Append(written++ > 0 ? "," : "").Append(areas[i].ToString(c)).Append(':').Append(partSims[i].ToString(c));
            }

            if (written == 0)
            {
                sb.Append('-');
            }

            return sb.ToString();
        }

        // A ring line as the replay splits it: f[0] the ring sequence, f[1] "Us", f[2] the object, f[3] the users, f[4]
        // the parts. A malformed line reads as nothing.
        public static bool Parse(string[] f, out ulong obj, out List<ulong> users, out List<uint> areas, out List<ulong> partSims)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            obj = 0uL;
            users = new List<ulong>();
            areas = new List<uint>();
            partSims = new List<ulong>();
            if (f == null || f.Length < 5 || f[1] != Kind || !ulong.TryParse(f[2], NumberStyles.None, c, out obj) || obj == 0uL)
            {
                obj = 0uL;
                return false;
            }

            if (f[3] != "-")
            {
                string[] parts = f[3].Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    ulong id;
                    if (!ulong.TryParse(parts[i], NumberStyles.None, c, out id) || id == 0uL)
                    {
                        return Fail(out obj, users, areas, partSims);
                    }

                    users.Add(id);
                }
            }

            if (f[4] != "-")
            {
                string[] parts = f[4].Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    int colon = parts[i].IndexOf(':');
                    uint area;
                    ulong sim;
                    if (colon <= 0 || !uint.TryParse(parts[i].Substring(0, colon), NumberStyles.None, c, out area)
                        || !ulong.TryParse(parts[i].Substring(colon + 1), NumberStyles.None, c, out sim) || sim == 0uL)
                    {
                        return Fail(out obj, users, areas, partSims);
                    }

                    areas.Add(area);
                    partSims.Add(sim);
                }
            }

            return true;
        }

        // The client's change to one list: want is what the server's object holds (in this game's ids), mine what the
        // client set there earlier, current what the list holds now. In: wanted and not there. Out: set by the client
        // and no longer wanted (an entry the client did not set is never taken out).
        public static void Reconcile(List<ulong> current, List<ulong> mine, List<ulong> want, List<ulong> add, List<ulong> remove)
        {
            Dictionary<ulong, bool> wanted = new Dictionary<ulong, bool>();
            for (int i = 0; i < want.Count; i++)
            {
                wanted[want[i]] = true;
            }

            Dictionary<ulong, bool> there = new Dictionary<ulong, bool>();
            for (int i = 0; i < current.Count; i++)
            {
                there[current[i]] = true;
            }

            foreach (ulong id in wanted.Keys)
            {
                if (!there.ContainsKey(id))
                {
                    add.Add(id);
                }
            }

            Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
            for (int i = 0; i < mine.Count; i++)
            {
                if (!wanted.ContainsKey(mine[i]) && there.ContainsKey(mine[i]) && !seen.ContainsKey(mine[i]))
                {
                    seen[mine[i]] = true;
                    remove.Add(mine[i]);
                }
            }
        }

        private static bool Fail(out ulong obj, List<ulong> users, List<uint> areas, List<ulong> partSims)
        {
            obj = 0uL;
            users.Clear();
            areas.Clear();
            partSims.Clear();
            return false;
        }
    }
}
