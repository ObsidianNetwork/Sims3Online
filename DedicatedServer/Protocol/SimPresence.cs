using System.Globalization;

namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>
/// W2 Sim identity step 3 (<c>library/engine/sim-identity-and-npc-creation.md</c>): one entry of the
/// server mod's presence journal (PROBE_SIM_PRESENCE, <c>SimPresenceWire</c>). A spawn carries where
/// the Sim appeared; a despawn only who left.
/// </summary>
public sealed record SimPresenceEntry(
    bool Spawned,
    ulong ObjectId,
    ulong SimDescriptionId,
    ulong LotId,
    Vector3Dto Position);

/// <summary>A PROBE_SIM_PRESENCE reply: the entries after the requested sequence number.</summary>
public sealed record SimPresenceReport(long Seq, bool More, long Lost, IReadOnlyList<SimPresenceEntry> Entries)
{
    /// <summary>Parses "seq=..;more=..;lost=..;entries=.." (SimPresenceWire); null when malformed.</summary>
    public static SimPresenceReport? Parse(string? report)
    {
        if (string.IsNullOrEmpty(report))
        {
            return null;
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string part in report.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                fields[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }

        if (!fields.TryGetValue("seq", out string? seqText)
            || !long.TryParse(seqText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq)
            || !fields.TryGetValue("entries", out string? entriesText))
        {
            return null;
        }

        long lost = fields.TryGetValue("lost", out string? lostText)
            && long.TryParse(lostText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0L;
        bool more = fields.TryGetValue("more", out string? moreText) && moreText == "1";
        List<SimPresenceEntry> entries = new();
        if (entriesText != "-")
        {
            foreach (string entry in entriesText.Split(','))
            {
                if (SimsOnlineServerHost.SimPresenceWire.TryParseEntry(entry, out bool spawned, out ulong objectId,
                        out ulong simDescriptionId, out ulong lotId, out float x, out float y, out float z))
                {
                    entries.Add(new SimPresenceEntry(spawned, objectId, simDescriptionId, lotId, new Vector3Dto(x, y, z)));
                }
            }
        }

        return new SimPresenceReport(seq, more, lost, entries);
    }
}
