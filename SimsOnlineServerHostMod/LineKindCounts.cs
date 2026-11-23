// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // Presentation lines counted by kind at the two ends of the stream: what the server handed its controller (push pages
    // and dumps) and what a client's replay received (every line of every batch, before any filter). A kind sent but
    // never received was lost between them; received but never applied, skipped or failed, was lost in the client.
    // The boot's first-join AW| lines left the server, but no client counter moved.
    internal static class LineKindCounts
    {
        internal const int MaxKinds = 64; // the stream has about 50 kinds; more counts under "?"
        internal const string Other = "?";

        // "seq|kind|..." -> kind; null for a line without one.
        internal static string KindOf(string line)
        {
            if (line == null)
            {
                return null;
            }

            int first = line.IndexOf('|');
            if (first < 0)
            {
                return null;
            }

            int second = line.IndexOf('|', first + 1);
            string kind = second < 0 ? line.Substring(first + 1) : line.Substring(first + 1, second - first - 1);
            return kind.Length == 0 ? null : kind;
        }

        internal static void Add(Dictionary<string, long> counts, string kind)
        {
            if (kind == null)
            {
                return;
            }

            if (!counts.ContainsKey(kind) && counts.Count >= MaxKinds)
            {
                kind = Other;
            }

            long seen;
            counts.TryGetValue(kind, out seen);
            counts[kind] = seen + 1;
        }

        internal static void AddLines(Dictionary<string, long> counts, IList<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                Add(counts, KindOf(lines[i]));
            }
        }

        // "AS:12/AW:5/P:40", kinds in ordinal order ('/' and ':' only: it rides in ';'- and ','-separated replies).
        internal static string Format(Dictionary<string, long> counts)
        {
            List<string> kinds = new List<string>(counts.Keys);
            kinds.Sort(StringComparer.Ordinal);
            string text = string.Empty;
            for (int i = 0; i < kinds.Count; i++)
            {
                text += (i == 0 ? string.Empty : "/") + kinds[i] + ":" + counts[kinds[i]].ToString(CultureInfo.InvariantCulture);
            }

            return text.Length == 0 ? "-" : text;
        }
    }

    // A client's replay skips by site and kind, with the last skipped line of each (SACS_RECORDER replay-skips <kind>).
    // The 64-row ring of the latest skips turns over within a minute of a busy join; these counts and lines do not.
    // Sites are a fixed set of source lines and kinds are bounded by LineKindCounts.Add, so this stays small.
    internal sealed class SkipsByKind
    {
        private readonly Dictionary<int, Dictionary<string, long>> mCounts = new Dictionary<int, Dictionary<string, long>>();
        private readonly Dictionary<int, Dictionary<string, string>> mLast = new Dictionary<int, Dictionary<string, string>>();

        internal void Add(int site, string line)
        {
            string kind = LineKindCounts.KindOf(line);
            if (kind == null)
            {
                return;
            }

            Dictionary<string, long> counts;
            if (!mCounts.TryGetValue(site, out counts))
            {
                counts = new Dictionary<string, long>();
                mCounts[site] = counts;
                mLast[site] = new Dictionary<string, string>();
            }

            LineKindCounts.Add(counts, kind);
            if (counts.ContainsKey(kind))
            {
                mLast[site][kind] = line; // a kind past the cap counts under "?" and keeps no line
            }
        }

        // One kind's skips by site, "6982:1,7960:3" in site order; "-" when that kind was never skipped.
        internal string Sites(string kind)
        {
            List<int> sites = new List<int>(mCounts.Keys);
            sites.Sort();
            string text = string.Empty;
            for (int i = 0; i < sites.Count; i++)
            {
                long seen;
                if (kind != null && mCounts[sites[i]].TryGetValue(kind, out seen))
                {
                    text += (text.Length == 0 ? string.Empty : ",") + sites[i].ToString(CultureInfo.InvariantCulture) + ":"
                        + seen.ToString(CultureInfo.InvariantCulture);
                }
            }

            return text.Length == 0 ? "-" : text;
        }

        // One kind's last skipped line at each site, as "site|line" rows (the ring's row form), in site order.
        internal List<string> LastLines(string kind)
        {
            List<int> sites = new List<int>(mLast.Keys);
            sites.Sort();
            List<string> rows = new List<string>();
            for (int i = 0; i < sites.Count; i++)
            {
                string line;
                if (kind != null && mLast[sites[i]].TryGetValue(kind, out line))
                {
                    rows.Add(sites[i].ToString(CultureInfo.InvariantCulture) + "|" + line);
                }
            }

            return rows;
        }

        // A ring row ("site|seq|kind|...") of this kind.
        internal static bool RowOfKind(string row, string kind)
        {
            int bar = row == null ? -1 : row.IndexOf('|');
            return bar >= 0 && kind != null && LineKindCounts.KindOf(row.Substring(bar + 1)) == kind;
        }
    }
}
