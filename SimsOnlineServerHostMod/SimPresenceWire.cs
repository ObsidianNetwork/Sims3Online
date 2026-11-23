// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    // W2 Sim identity step 3 (internal documentation): the server mod journals
    // Sims entering and leaving its world. PROBE_SIM_PRESENCE|corrId|sinceSeq replies AUTONOMY_PROBE ok
    // with "seq=<latest>;more=<0|1>;lost=<n>;entries=<entry>,<entry>,..." ("-" when none), at most
    // MaxEntriesPerReply entries after sinceSeq. Entries:
    //   spawn   "+<objectId>:<simDescriptionId>:<lotId>:<x>:<y>:<z>"
    //   despawn "-<objectId>:<simDescriptionId>"
    // Shared by the mod handler (HandleProbeSimPresence) and the controller parser (SimPresenceReport).
    public static class SimPresenceWire
    {
        public const int MaxEntriesPerReply = 24;

        public static string FormatSpawn(ulong objectId, ulong simDescriptionId, ulong lotId, float x, float y, float z)
        {
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            return "+" + objectId.ToString(c) + ":" + simDescriptionId.ToString(c) + ":" + lotId.ToString(c) + ":"
                + x.ToString("R", c) + ":" + y.ToString("R", c) + ":" + z.ToString("R", c);
        }

        public static string FormatDespawn(ulong objectId, ulong simDescriptionId)
        {
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            return "-" + objectId.ToString(c) + ":" + simDescriptionId.ToString(c);
        }

        public static string FormatReply(long seq, bool more, long lost, System.Collections.Generic.IList<string> entries)
        {
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append("seq=").Append(seq.ToString(c)).Append(";more=").Append(more ? "1" : "0")
                .Append(";lost=").Append(lost.ToString(c)).Append(";entries=");
            if (entries == null || entries.Count == 0)
            {
                text.Append('-');
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(',');
                    }

                    text.Append(entries[i]);
                }
            }

            return text.ToString();
        }

        // False when the entry is malformed. A despawn leaves lot and position at zero.
        public static bool TryParseEntry(string entry, out bool spawned, out ulong objectId, out ulong simDescriptionId,
            out ulong lotId, out float x, out float y, out float z)
        {
            spawned = false;
            objectId = 0uL;
            simDescriptionId = 0uL;
            lotId = 0uL;
            x = 0f;
            y = 0f;
            z = 0f;
            if (entry == null || entry.Length < 2 || (entry[0] != '+' && entry[0] != '-'))
            {
                return false;
            }

            spawned = entry[0] == '+';
            string[] f = entry.Substring(1).Split(':');
            System.Globalization.NumberStyles n = System.Globalization.NumberStyles.None;
            System.Globalization.NumberStyles real = System.Globalization.NumberStyles.Float;
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            if (f.Length != (spawned ? 6 : 2)
                || !ulong.TryParse(f[0], n, c, out objectId)
                || !ulong.TryParse(f[1], n, c, out simDescriptionId))
            {
                return false;
            }

            if (!spawned)
            {
                return true;
            }

            return ulong.TryParse(f[2], n, c, out lotId)
                && float.TryParse(f[3], real, c, out x)
                && float.TryParse(f[4], real, c, out y)
                && float.TryParse(f[5], real, c, out z);
        }
    }
}
