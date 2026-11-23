using System.Globalization;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>Card 61's bridge-only request, using integer centimetres and a server-only frustum.</summary>
public readonly record struct RoadPlaceRequest(ulong Corr, ulong ObjectId, int X, int Y, int Z, int LaneKind,
    int DistanceCm, int RadiusCm, string View)
{
    public string Arguments => string.Join("|", ObjectId.ToString(CultureInfo.InvariantCulture), X.ToString(CultureInfo.InvariantCulture),
        Y.ToString(CultureInfo.InvariantCulture), Z.ToString(CultureInfo.InvariantCulture), LaneKind.ToString(CultureInfo.InvariantCulture),
        DistanceCm.ToString(CultureInfo.InvariantCulture), RadiusCm.ToString(CultureInfo.InvariantCulture), View);

    public static bool TryParse(string line, out RoadPlaceRequest request)
    {
        request = default;
        string[] p = line.TrimStart('\uFEFF').Split('|');
        if (p.Length != 11 || p[0] != "EVENT" || p[1] != "RoadPlaceRequest"
            || !ulong.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong corr) || corr == 0
            || !ulong.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out ulong obj))
            return false;
        int[] v = new int[6];
        for (int i = 0; i < v.Length; i++)
            if (!Integer(p[4 + i], out v[i])) return false;
        if (v.Take(3).Any(n => n < -1_000_000 || n > 1_000_000) || v[3] < 0 || v[3] > 255
            || v[4] < 0 || v[4] > 1_000_000 || v[5] < 0 || v[5] > 100_000 || (obj == 0 && v[5] != 0)
            || !ValidView(p[10]))
            return false;
        request = new(corr, obj, v[0], v[1], v[2], v[3], v[4], v[5], p[10]);
        return true;
    }

    private static bool Integer(string s, out int v) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)
        && s.Length > 0 && s[0] != '+' && s != "-0"
        && (s[0] != '-' || s.Length > 1)
        && s.Skip(s[0] == '-' ? 1 : 0).All(c => c is >= '0' and <= '9');

    public static bool ValidView(string text)
    {
        if (text == "none") return true;
        string[] parts = text.Split(',');
        if (parts.Length != 13) return false;
        long[] v = new long[13];
        for (int i = 0; i < v.Length; i++)
        {
            if (!Integer(parts[i], out int n)) return false;
            v[i] = n;
        }
        if (v.Take(9).Any(n => n < -1_000_000 || n > 1_000_000)
            || v[9] < 1 || v[9] > 179999 || v[10] < 1 || v[10] > 10000000
            || v[11] < 0 || v[11] >= v[12] || v[12] > 4000000) return false;
        long f = v[3] * v[3] + v[4] * v[4] + v[5] * v[5];
        long u = v[6] * v[6] + v[7] * v[7] + v[8] * v[8];
        long dot = v[3] * v[6] + v[4] * v[7] + v[5] * v[8];
        return Math.Abs(f - 1000000000000L) <= 2000000 && Math.Abs(u - 1000000000000L) <= 2000000 && Math.Abs(dot) <= 1000000;
    }
}
