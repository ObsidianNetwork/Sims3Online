// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    // W2 part B (internal documentation): one Sim's mirrored visible state -- its six
    // motives and its buffs -- as one text entry. The server's PROBE_SIM_STATE reply carries entries
    // joined by ';'; the client's APPLY_SIM_STATE carries one entry. Shared by the mod (format on probe,
    // parse on apply) and the controller (parse probe replies, format apply commands).
    //
    //   <simId>:<m0>,<m1>,<m2>,<m3>,<m4>,<m5>:<guid>/<origin>+<guid>/<origin>   ("-" for no buffs)
    //
    // Motive order is MotiveNames (CommodityKind names). Values use 2 decimals, invariant culture.
    public static class SimStateWire
    {
        public static readonly string[] MotiveNames = new string[] { "Hunger", "Energy", "Bladder", "Social", "Hygiene", "Fun" };

        // Sims per PROBE_SIM_STATE: keeps the hex-encoded reply inside one bridge slot.
        public const int MaxSimsPerProbe = 8;

        public static string FormatEntry(ulong simId, float[] motives, ulong[] buffGuids, ulong[] buffOrigins)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            text.Append(simId.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':');
            for (int i = 0; i < MotiveNames.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                float value = motives != null && i < motives.Length ? motives[i] : 0f;
                text.Append(value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }

            text.Append(':');
            if (buffGuids == null || buffGuids.Length == 0)
            {
                text.Append('-');
            }
            else
            {
                for (int i = 0; i < buffGuids.Length; i++)
                {
                    if (i > 0)
                    {
                        text.Append('+');
                    }

                    ulong origin = buffOrigins != null && i < buffOrigins.Length ? buffOrigins[i] : 0UL;
                    text.Append(buffGuids[i].ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Append('/')
                        .Append(origin.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            return text.ToString();
        }

        // Returns false (outputs empty) when the entry is malformed.
        public static bool TryParseEntry(string entry, out ulong simId, out float[] motives, out ulong[] buffGuids, out ulong[] buffOrigins)
        {
            simId = 0;
            motives = new float[0];
            buffGuids = new ulong[0];
            buffOrigins = new ulong[0];
            if (entry == null)
            {
                return false;
            }

            string[] fields = entry.Split(':');
            if (fields.Length != 3)
            {
                return false;
            }

            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!ulong.TryParse(fields[0], System.Globalization.NumberStyles.None, inv, out simId))
            {
                return false;
            }

            string[] values = fields[1].Split(',');
            if (values.Length != MotiveNames.Length)
            {
                return false;
            }

            float[] parsedMotives = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.TryParse(values[i], System.Globalization.NumberStyles.Float, inv, out parsedMotives[i]))
                {
                    return false;
                }
            }

            ulong[] guids = new ulong[0];
            ulong[] origins = new ulong[0];
            if (fields[2] != "-")
            {
                string[] buffs = fields[2].Split('+');
                guids = new ulong[buffs.Length];
                origins = new ulong[buffs.Length];
                for (int i = 0; i < buffs.Length; i++)
                {
                    string[] pair = buffs[i].Split('/');
                    if (pair.Length != 2
                        || !ulong.TryParse(pair[0], System.Globalization.NumberStyles.None, inv, out guids[i])
                        || !ulong.TryParse(pair[1], System.Globalization.NumberStyles.None, inv, out origins[i]))
                    {
                        return false;
                    }
                }
            }

            motives = parsedMotives;
            buffGuids = guids;
            buffOrigins = origins;
            return true;
        }
    }
}
