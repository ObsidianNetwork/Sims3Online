using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>
/// the slow-changing details of a claimed Sim that a client's UI and pie menus read from its own copy:
/// job (career guid, level, branch), household funds and skill levels. Wire form is one
/// <see cref="SimDetailsWire"/> entry (PROBE_SIM_DETAILS reply, APPLY_SIM_DETAILS).
/// </summary>
public sealed record SimDetailsSample(ulong SimObjectId, ulong CareerGuid, int CareerLevel, string CareerBranch, int Funds, string Skills)
{
    public string ToWireEntry() =>
        SimDetailsWire.FormatEntry(SimObjectId, CareerGuid, CareerLevel, CareerBranch, Funds, Skills);

    public static bool TryParseWireEntry(string entry, out SimDetailsSample? sample)
    {
        sample = null;
        if (!SimDetailsWire.TryParseEntry(entry, out ulong simId, out ulong careerGuid, out int level, out string branch, out int funds, out string skills))
        {
            return false;
        }

        sample = new SimDetailsSample(simId, careerGuid, level, branch, funds, skills);
        return true;
    }

    /// <summary>Parses a PROBE_SIM_DETAILS reply ("-" or entries joined by ';'), skipping bad entries.</summary>
    public static IReadOnlyList<SimDetailsSample> ParseProbeReport(string? report)
    {
        List<SimDetailsSample> samples = new();
        if (string.IsNullOrWhiteSpace(report) || report.Trim() == "-")
        {
            return samples;
        }

        foreach (string entry in report.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseWireEntry(entry, out SimDetailsSample? sample))
            {
                samples.Add(sample!);
            }
        }

        return samples;
    }
}
