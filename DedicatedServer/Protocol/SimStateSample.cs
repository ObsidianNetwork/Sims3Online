using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Protocol;

// SimBuffDto moved to SimsOnline.Protocol (card 57 ruling 4: it rides ServerEvent.SimStateChanged);
// SimStateSample stays here because it parses the SimStateWire bridge line.

/// <summary>
/// W2 part B (internal documentation): the visible state of one Sim the server mirrors to
/// clients that do not own it: its six motives in <see cref="SimStateWire.MotiveNames"/> order and its
/// buffs. Wire form is one <see cref="SimStateWire"/> entry (PROBE_SIM_STATE reply, APPLY_SIM_STATE).
/// </summary>
public sealed record SimStateSample(ulong SimObjectId, IReadOnlyList<float> Motives, IReadOnlyList<SimBuffDto> Buffs)
{
    public string ToWireEntry() =>
        SimStateWire.FormatEntry(
            SimObjectId,
            Motives.ToArray(),
            Buffs.Select(b => b.Guid).ToArray(),
            Buffs.Select(b => b.Origin).ToArray());

    public static bool TryParseWireEntry(string entry, out SimStateSample? sample)
    {
        sample = null;
        if (!SimStateWire.TryParseEntry(entry, out ulong simId, out float[] motives, out ulong[] guids, out ulong[] origins))
        {
            return false;
        }

        List<SimBuffDto> buffs = new(guids.Length);
        for (int i = 0; i < guids.Length; i++)
        {
            buffs.Add(new SimBuffDto(guids[i], origins[i]));
        }

        sample = new SimStateSample(simId, motives, buffs);
        return true;
    }

    /// <summary>Parses a PROBE_SIM_STATE reply ("-" or entries joined by ';'), skipping bad entries.</summary>
    public static IReadOnlyList<SimStateSample> ParseProbeReport(string? report)
    {
        if (string.IsNullOrWhiteSpace(report) || report.Trim() == "-")
        {
            return Array.Empty<SimStateSample>();
        }

        List<SimStateSample> samples = new();
        foreach (string entry in report.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseWireEntry(entry, out SimStateSample? sample) && sample != null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }
}
