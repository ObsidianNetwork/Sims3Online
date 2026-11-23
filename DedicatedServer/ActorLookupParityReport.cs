using System.Globalization;

namespace SimsOnline.DedicatedServer;

/// <summary>
/// M4 P4.0.1: structured view of the actor-lookup parity probe report emitted by the
/// script DLL. The script DLL sends a semicolon-delimited key=value string in the
/// ACTOR_LOOKUP_PROBE wire response body:
///   total=N;resolved=R;failed=F;failures=id1,id2,...
/// failures is empty when F == 0. The first 10 failing ObjectIds are reported.
/// </summary>
public sealed record ActorLookupParityReport(int Total, int Resolved, int Failed, IReadOnlyList<ulong> FailingObjectIds)
{
    public bool IsParity => Failed == 0 && Total > 0;

    public static ActorLookupParityReport Parse(string raw)
    {
        if (raw is null) throw new ArgumentNullException(nameof(raw));

        int total = 0;
        int resolved = 0;
        int failed = 0;
        List<ulong> failures = new();

        foreach (string field in raw.Split(';'))
        {
            if (string.IsNullOrEmpty(field)) continue;
            int eq = field.IndexOf('=');
            if (eq < 0) continue;
            string key = field.Substring(0, eq);
            string value = field.Substring(eq + 1);
            switch (key)
            {
                case "total":
                    total = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "resolved":
                    resolved = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "failed":
                    failed = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "failures":
                    if (!string.IsNullOrEmpty(value))
                    {
                        foreach (string id in value.Split(','))
                        {
                            if (ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
                            {
                                failures.Add(parsed);
                            }
                        }
                    }
                    break;
            }
        }

        return new ActorLookupParityReport(total, resolved, failed, failures);
    }
}
