using System.Globalization;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer;

public sealed record StateInjectionProbeReport(
    string Method,
    ulong SimObjectId,
    Vector3Dto Before,
    Vector3Dto Target,
    Vector3Dto After,
    ulong BeforeInteractionId,
    ulong AfterInteractionId,
    float DistanceToTarget)
{
    public static StateInjectionProbeReport Parse(string report)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string part in report.Split(';'))
        {
            int index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            fields[part[..index]] = part[(index + 1)..];
        }

        return new StateInjectionProbeReport(
            Required(fields, "method"),
            ulong.Parse(Required(fields, "sim"), CultureInfo.InvariantCulture),
            ParseVector(Required(fields, "before")),
            ParseVector(Required(fields, "target")),
            ParseVector(Required(fields, "after")),
            ulong.Parse(Required(fields, "beforeInteraction"), CultureInfo.InvariantCulture),
            ulong.Parse(Required(fields, "afterInteraction"), CultureInfo.InvariantCulture),
            float.Parse(Required(fields, "distance"), CultureInfo.InvariantCulture));
    }

    public bool AfterPositionMatchesTarget(float maximumDistance) =>
        Distance(After, Target) <= maximumDistance && DistanceToTarget <= maximumDistance;

    private static string Required(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? value) || value.Length == 0)
        {
            throw new FormatException($"State injection report is missing '{key}'.");
        }

        return value;
    }

    private static Vector3Dto ParseVector(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 3)
        {
            throw new FormatException($"State injection vector '{value}' should have three coordinates.");
        }

        return new Vector3Dto(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private static double Distance(Vector3Dto left, Vector3Dto right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        double z = left.Z - right.Z;
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }
}
