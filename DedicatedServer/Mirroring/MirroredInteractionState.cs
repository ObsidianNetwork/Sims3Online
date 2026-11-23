using System.Globalization;

namespace SimsOnline.DedicatedServer.Mirroring;

public sealed record MirroredInteractionState(IReadOnlyDictionary<string, string> Fields)
{
    public string Field(string name) => Fields.TryGetValue(name, out string? value) ? value : string.Empty;

    public bool IsSeatedInContainer(ulong containerObjectId)
    {
        string postureType = Field("postureType");
        if (!postureType.Contains("SittingPosture", StringComparison.Ordinal))
        {
            return false;
        }

        if (!ulong.TryParse(Field("postureContainerObjectId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong actual))
        {
            return false;
        }

        return actual == containerObjectId;
    }

    public static MirroredInteractionState Parse(string raw)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(raw))
        {
            foreach (string part in raw.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                fields[part[..eq]] = part[(eq + 1)..];
            }
        }

        return new MirroredInteractionState(fields);
    }
}
