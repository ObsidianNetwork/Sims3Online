using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // Diagnostic only: no engine types, so the same bounds and paging run in the offline tests.
    internal sealed class SacsRequestTrace
    {
        internal const int Capacity = 256;
        internal const int FailureCapacity = 64;
        internal bool Enabled;
        private readonly string[] mRows = new string[Capacity];
        private readonly string[] mFailures = new string[FailureCapacity];
        internal long Count;
        internal long Failures;

        internal void Add(string row, int result)
        {
            mRows[(int)(Count++ % Capacity)] = row;
            if (result < 0)
            {
                mFailures[(int)(Failures++ % FailureCapacity)] = row;
            }
        }

        internal void Clear()
        {
            Array.Clear(mRows, 0, mRows.Length);
            Array.Clear(mFailures, 0, mFailures.Length);
            Count = 0;
            Failures = 0;
        }

        internal string Read(bool failures, int offset, int maxChars)
        {
            string[] ring = failures ? mFailures : mRows;
            long count = failures ? Failures : Count;
            int available = (int)Math.Min(count, ring.Length);
            int next = Math.Max(0, offset);
            List<string> rows = new List<string>();
            int chars = 0;
            while (next < available)
            {
                string row = ring[(int)((count - 1 - next) % ring.Length)];
                if (chars + row.Length + 1 > maxChars)
                {
                    break;
                }
                rows.Add(row);
                chars += row.Length + 1;
                next++;
            }
            return "enabled=" + (Enabled ? "1" : "0") + ";requests=" + Count.ToString(CultureInfo.InvariantCulture)
                + ";failures=" + Failures.ToString(CultureInfo.InvariantCulture)
                + ";next=" + (next < available ? next : -1).ToString(CultureInfo.InvariantCulture)
                + "\n" + string.Join("\n", rows.ToArray());
        }

        internal static string Snapshot(uint[] w, uint error, uint synchronized)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string text = "error:" + error.ToString(c) + ",sync:" + synchronized.ToString(c)
                + ",walker:" + w[1].ToString("x8", c)
                + ",current:" + w[2].ToString("x8", c) + "/" + w[3].ToString(c)
                + ",next:" + w[4].ToString("x8", c) + "/" + w[5].ToString(c)
                + ",destination:" + w[6].ToString(c) + ",flags:" + w[7].ToString(c)
                + ",mode:" + w[8].ToString(c) + ",queued:" + w[9].ToString(c) + "[";
            for (int i = 0; i < 4 && i < w[9]; i++)
            {
                text += (i == 0 ? "" : ",") + w[10 + 2 * i].ToString(c) + "/" + w[11 + 2 * i].ToString(c);
            }
            return text + (w[9] > 4 ? ",...]" : "]");
        }
    }
}
