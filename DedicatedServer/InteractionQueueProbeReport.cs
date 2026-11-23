using System.Globalization;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// M4 P4.1.1.1: parsed view of the SIM_INTERACTION_QUEUE_PROBE response report.
/// Mirrors the ActorLookupParityReport pattern: deterministic Parse from the
/// script-DLL's "key=value;key=value" report string.
///
/// Wire report format: "sim=&lt;id&gt;;queueCount=&lt;n&gt;;current=&lt;typeName&gt;"
/// </summary>
public sealed record InteractionQueueProbeReport(ulong SimObjectId, int QueueCount, string CurrentKey)
{
    public static InteractionQueueProbeReport Parse(string report)
    {
        ulong sim = 0UL;
        int count = 0;
        string current = string.Empty;

        foreach (string field in report.Split(';'))
        {
            int eq = field.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            string key = field.Substring(0, eq);
            string val = field.Substring(eq + 1);

            if (key == "sim")
            {
                if (ulong.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedSim))
                {
                    sim = parsedSim;
                }
            }
            else if (key == "queueCount")
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount))
                {
                    count = parsedCount;
                }
            }
            else if (key == "current")
            {
                current = val;
            }
        }

        return new InteractionQueueProbeReport(sim, count, current);
    }

    /// <summary>True if the current/head interaction key contains the given substring.</summary>
    public bool HasQueuedInteraction(string substring)
    {
        return QueueCount >= 1 && CurrentKey.Contains(substring, System.StringComparison.Ordinal);
    }
}