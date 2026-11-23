using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// One half of the P2.5 load-path comparison probe. Captures the result of a single load attempt
/// (either the existing direct path or the menu-API path) so the two halves can be compared.
/// </summary>
public sealed record LoadPathOutcome(
    string Label,
    bool Success,
    string? Error,
    TimeSpan Elapsed,
    int? SimCount,
    int? ObjectCount,
    string? WorldName,
    string? SaveName,
    long? Tick)
{
    public static LoadPathOutcome FromSnapshot(string label, SimulationSnapshot snapshot, TimeSpan elapsed) =>
        new(label, true, null, elapsed, snapshot.Sims.Count, snapshot.Objects.Count, snapshot.WorldName, snapshot.SaveName, snapshot.Tick);

    public static LoadPathOutcome Failure(string label, string error, TimeSpan elapsed) =>
        new(label, false, error, elapsed, null, null, null, null, null);
}

/// <summary>
/// Compares the two load-path outcomes captured by the P2.5 probe and reports divergences.
/// IsEquivalent is true only when both paths succeeded and produced matching world state
/// (sim count, object count, world name, save name all equal AND non-empty).
/// </summary>
public sealed record LoadPathComparisonReport(LoadPathOutcome Direct, LoadPathOutcome Menu)
{
    public bool IsEquivalent =>
        Direct.Success && Menu.Success &&
        Direct.SimCount.HasValue && Direct.SimCount.Value > 0 &&
        Menu.SimCount.HasValue && Menu.SimCount.Value > 0 &&
        Direct.ObjectCount.HasValue && Direct.ObjectCount.Value > 0 &&
        Menu.ObjectCount.HasValue && Menu.ObjectCount.Value > 0 &&
        Direct.SimCount == Menu.SimCount &&
        Direct.ObjectCount == Menu.ObjectCount &&
        string.Equals(Direct.WorldName, Menu.WorldName, StringComparison.Ordinal) &&
        string.Equals(Direct.SaveName, Menu.SaveName, StringComparison.Ordinal);

    public IReadOnlyList<string> Divergences()
    {
        List<string> result = new();
        if (!Direct.Success)
        {
            result.Add(FormattableString.Invariant($"direct path failed: {Direct.Error ?? "(unknown)"}"));
        }

        if (!Menu.Success)
        {
            result.Add(FormattableString.Invariant($"menu path failed: {Menu.Error ?? "(unknown)"}"));
        }

        if (Direct.Success && Menu.Success)
        {
            if (Direct.SimCount != Menu.SimCount)
            {
                result.Add(FormattableString.Invariant($"sim count diverged: direct={Direct.SimCount} menu={Menu.SimCount}"));
            }

            if (Direct.ObjectCount != Menu.ObjectCount)
            {
                result.Add(FormattableString.Invariant($"object count diverged: direct={Direct.ObjectCount} menu={Menu.ObjectCount}"));
            }

            if (!string.Equals(Direct.WorldName, Menu.WorldName, StringComparison.Ordinal))
            {
                result.Add(FormattableString.Invariant($"world name diverged: direct=\"{Direct.WorldName}\" menu=\"{Menu.WorldName}\""));
            }

            if (!string.Equals(Direct.SaveName, Menu.SaveName, StringComparison.Ordinal))
            {
                result.Add(FormattableString.Invariant($"save name diverged: direct=\"{Direct.SaveName}\" menu=\"{Menu.SaveName}\""));
            }

            if (Direct.SimCount.HasValue && Direct.SimCount.Value <= 0)
            {
                result.Add("direct path returned a non-positive sim count.");
            }

            if (Menu.SimCount.HasValue && Menu.SimCount.Value <= 0)
            {
                result.Add("menu path returned a non-positive sim count.");
            }
        }

        return result;
    }

    public string ElapsedReport() =>
        FormattableString.Invariant(
            $"elapsed: direct={Direct.Elapsed.TotalSeconds:F2}s menu={Menu.Elapsed.TotalSeconds:F2}s delta={(Menu.Elapsed - Direct.Elapsed).TotalSeconds:F2}s");
}
