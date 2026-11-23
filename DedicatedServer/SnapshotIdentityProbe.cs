using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer;

public static class SnapshotIdentityProbe
{
    public static SnapshotIdentitySelection Select(SimulationSnapshot snapshot, int sampleSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);

        List<SimIdentity> sims = snapshot.Sims
            .Where(sim => sim.ObjectId != 0 && !string.IsNullOrWhiteSpace(sim.Name))
            .GroupBy(sim => sim.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group =>
            {
                SimSnapshot sim = group.Single();
                return new SimIdentity(sim.Name, sim.ObjectId, sim.HouseholdId, sim.Position);
            })
            .OrderBy(sim => sim.Name, StringComparer.Ordinal)
            .Take(sampleSize)
            .ToList();

        List<ObjectIdentity> objects = snapshot.Objects
            .Where(obj => obj.ObjectId != 0 && !string.IsNullOrWhiteSpace(obj.ScriptClass))
            .Select(obj => new ObjectIdentity(ObjectKey(obj), obj.ObjectId, obj.ScriptClass, obj.Position, obj.LotId))
            .GroupBy(obj => obj.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(obj => obj.ScriptClass, StringComparer.Ordinal)
            .ThenBy(obj => obj.Key, StringComparer.Ordinal)
            .Take(sampleSize)
            .ToList();

        return new SnapshotIdentitySelection(sampleSize, sims, objects);
    }

    public static SnapshotIdentityComparison Compare(SnapshotIdentitySelection baseline, SimulationSnapshot current)
    {
        Dictionary<string, SimSnapshot> currentSims = current.Sims
            .Where(sim => !string.IsNullOrWhiteSpace(sim.Name))
            .GroupBy(sim => sim.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        Dictionary<string, ObjectSnapshot> currentObjects = current.Objects
            .Where(obj => !string.IsNullOrWhiteSpace(obj.ScriptClass))
            .GroupBy(ObjectKey, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        List<IdentityMatch> simMatches = new();
        foreach (SimIdentity sim in baseline.Sims)
        {
            simMatches.Add(currentSims.TryGetValue(sim.Name, out SimSnapshot? currentSim)
                ? IdentityMatch.Found(sim.Name, sim.ObjectId, currentSim.ObjectId)
                : IdentityMatch.Missing(sim.Name, sim.ObjectId));
        }

        List<IdentityMatch> objectMatches = new();
        foreach (ObjectIdentity obj in baseline.Objects)
        {
            objectMatches.Add(currentObjects.TryGetValue(obj.Key, out ObjectSnapshot? currentObj)
                ? IdentityMatch.Found(obj.Label, obj.ObjectId, currentObj.ObjectId)
                : IdentityMatch.Missing(obj.Label, obj.ObjectId));
        }

        return new SnapshotIdentityComparison(baseline.RequestedSampleSize, simMatches, objectMatches);
    }

    private static string ObjectKey(ObjectSnapshot obj)
    {
        return string.Join(
            "|",
            obj.ScriptClass,
            obj.LotId?.ToString(CultureInfo.InvariantCulture) ?? "-",
            obj.Position.X.ToString("R", CultureInfo.InvariantCulture),
            obj.Position.Y.ToString("R", CultureInfo.InvariantCulture),
            obj.Position.Z.ToString("R", CultureInfo.InvariantCulture));
    }
}

public sealed record SnapshotIdentitySelection(
    int RequestedSampleSize,
    IReadOnlyList<SimIdentity> Sims,
    IReadOnlyList<ObjectIdentity> Objects)
{
    public bool HasRequiredSampleSize =>
        Sims.Count == RequestedSampleSize &&
        Objects.Count == RequestedSampleSize;
}

public sealed record SimIdentity(
    string Name,
    ulong ObjectId,
    ulong HouseholdId,
    Vector3Dto Position);

public sealed record ObjectIdentity(
    string Key,
    ulong ObjectId,
    string ScriptClass,
    Vector3Dto Position,
    ulong? LotId)
{
    public string Label => $"{ScriptClass} @ ({Position.X:R},{Position.Y:R},{Position.Z:R}) lot={LotId?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
}

public sealed record SnapshotIdentityComparison(
    int RequestedSampleSize,
    IReadOnlyList<IdentityMatch> Sims,
    IReadOnlyList<IdentityMatch> Objects)
{
    public bool AllStable =>
        Sims.Count == RequestedSampleSize &&
        Objects.Count == RequestedSampleSize &&
        Sims.All(match => match.IsStable) &&
        Objects.All(match => match.IsStable);
}

public sealed record IdentityMatch(
    string Label,
    ulong BaselineObjectId,
    ulong? CurrentObjectId)
{
    public bool IsMissing => CurrentObjectId is null;

    public bool IsStable => CurrentObjectId == BaselineObjectId;

    public static IdentityMatch Found(string label, ulong baselineObjectId, ulong currentObjectId) =>
        new(label, baselineObjectId, currentObjectId);

    public static IdentityMatch Missing(string label, ulong baselineObjectId) =>
        new(label, baselineObjectId, null);
}
