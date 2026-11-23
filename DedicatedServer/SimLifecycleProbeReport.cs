using System.Globalization;

namespace SimsOnline.DedicatedServer;

public sealed record SimLifecycleProbeReport(
    string Api,
    SimLifecycleSample WorldLoad,
    SimLifecycleSample LoadingScreenDisposed,
    SimLifecycleSample FirstSnapshotRequest,
    SimLifecycleSample Current)
{
    public bool ReadyHookMatchesFirstSnapshot =>
        LoadingScreenDisposed.Seen &&
        FirstSnapshotRequest.Seen &&
        LoadingScreenDisposed.SnapshotSims > 0 &&
        LoadingScreenDisposed.SnapshotSims == FirstSnapshotRequest.SnapshotSims;

    public bool FirstSnapshotMatchesControllerSnapshot(int controllerSnapshotSims) =>
        FirstSnapshotRequest.Seen &&
        FirstSnapshotRequest.SnapshotSims == controllerSnapshotSims &&
        controllerSnapshotSims > 0;

    public static SimLifecycleProbeReport Parse(string report)
    {
        string api = string.Empty;
        Dictionary<string, SimLifecycleSample> samples = new(StringComparer.Ordinal);

        foreach (string part in report.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = part[..equals];
            string value = part[(equals + 1)..];
            if (key == "api")
            {
                api = value;
                continue;
            }

            samples[key] = SimLifecycleSample.Parse(key, value);
        }

        return new SimLifecycleProbeReport(
            api,
            Required(samples, "worldLoad"),
            Required(samples, "loadingScreenDisposed"),
            Required(samples, "firstSnapshotRequest"),
            Required(samples, "current"));
    }

    private static SimLifecycleSample Required(Dictionary<string, SimLifecycleSample> samples, string key)
    {
        if (!samples.TryGetValue(key, out SimLifecycleSample? sample))
        {
            throw new FormatException($"Sim lifecycle report is missing '{key}'.");
        }

        return sample;
    }
}

public sealed record SimLifecycleSample(
    string Stage,
    bool Seen,
    long Tick,
    int Actors,
    int VisibleActors,
    int SnapshotSims)
{
    public static SimLifecycleSample Parse(string stage, string value)
    {
        string[] fields = value.Split(',');
        if (fields.Length != 5)
        {
            throw new FormatException($"Sim lifecycle sample '{stage}' has {fields.Length} fields.");
        }

        return new SimLifecycleSample(
            stage,
            fields[0] == "1",
            long.Parse(fields[1], CultureInfo.InvariantCulture),
            int.Parse(fields[2], CultureInfo.InvariantCulture),
            int.Parse(fields[3], CultureInfo.InvariantCulture),
            int.Parse(fields[4], CultureInfo.InvariantCulture));
    }
}
