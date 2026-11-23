using System.Globalization;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// card 55 R3: a server-mod walk asks for a Rust plan. The mod posts
/// <c>EVENT|RoutePlanRequest|sim|sx|sy|sz|gx|gy|gz|corr</c> on the memory bridge (floats invariant, metres); the controller
/// answers with <c>SACS_RECORDER rust-plan &lt;sim&gt; &lt;x,y,z,...&gt; &lt;corr&gt;</c> or <c>rust-plan-none &lt;sim&gt; &lt;corr&gt;</c>. Mod side:
/// <c>RustRoutePlan.BridgeLine</c>; the two change together. This is bridge traffic only: it is never forwarded to
/// clients and is not part of the public protocol.
/// </summary>
public readonly record struct RoutePlanRequest(ulong Sim, float StartX, float StartY, float StartZ, float GoalX, float GoalY, float GoalZ, ulong Corr = 0)
{
    public const string Kind = "RoutePlanRequest";

    public static bool IsRequest(string line) =>
        line.TrimStart('\uFEFF').StartsWith("EVENT|" + Kind + "|", StringComparison.Ordinal);

    public static bool TryParse(string line, out RoutePlanRequest request)
    {
        request = default;
        string[] p = line.TrimStart('\uFEFF').Split('|');
        if (p.Length < 9 || p[0] != "EVENT" || p[1] != Kind || !ulong.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sim) || sim == 0)
        {
            return false;
        }

        float[] v = new float[6];
        for (int i = 0; i < 6; i++)
        {
            if (!float.TryParse(p[3 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
            {
                return false;
            }
        }

        ulong corr = 0;
        if (p.Length > 9 && (!ulong.TryParse(p[9], NumberStyles.None, CultureInfo.InvariantCulture, out corr) || corr == 0))
            return false;
        request = new RoutePlanRequest(sim, v[0], v[1], v[2], v[3], v[4], v[5], corr);
        return true;
    }

    /// <summary>The x,y,z,x,y,z,... list of the rust-plan verb.</summary>
    public static string FormatPoints(IReadOnlyList<float> xyz)
    {
        string[] parts = new string[xyz.Count];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = xyz[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return string.Join(",", parts);
    }
}
