using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Authority;

/// <summary>
/// sustained clock sync: decides when the server should re-broadcast
/// <see cref="ServerEvent.WorldTimeChanged"/> so mirror clients re-apply SET_WORLD_TIME.
/// One-shot set/join replay aligns at a point in time; independent TS3 sim ticks drift apart.
/// </summary>
public static class WorldTimeReconcilePolicy
{
    /// <summary>Minimum game-hours drift before an off-schedule reconcile (about 3 sim-minutes).</summary>
    public const float DefaultMinGameHoursDrift = 0.05f;

    /// <summary>Minimum wall-clock spacing between reconcile broadcasts (limits bridge traffic).</summary>
    public static readonly TimeSpan DefaultMinWallInterval = TimeSpan.FromSeconds(30);

    public static bool TryParseDiagClock(string report, out WorldTime time)
    {
        time = default;
        if (string.IsNullOrEmpty(report))
        {
            return false;
        }

        if (!TryParseSemicolonField(report, "clockDay=", out int day)
            || !TryParseSemicolonField(report, "clockHours=", out int hours)
            || !TryParseSemicolonField(report, "clockMinutes=", out int minutes))
        {
            return false;
        }

        if (day is < 0 or > 6 || hours is < 0 or > 23 || minutes is < 0 or > 59)
        {
            return false;
        }

        time = new WorldTime(day, hours, minutes);
        return true;
    }

    public static bool ShouldReconcile(
        WorldTime authoritative,
        WorldTime? lastBroadcast,
        TimeSpan wallSinceLastBroadcast,
        float minGameHoursDrift = DefaultMinGameHoursDrift,
        TimeSpan? minWallInterval = null)
    {
        TimeSpan wallInterval = minWallInterval ?? DefaultMinWallInterval;
        if (lastBroadcast is null)
        {
            return true;
        }

        float drift = GameHoursDrift(authoritative, lastBroadcast.Value);
        if (authoritative.Day != lastBroadcast.Value.Day)
        {
            return wallSinceLastBroadcast >= wallInterval || drift >= minGameHoursDrift;
        }

        if (drift >= minGameHoursDrift)
        {
            return true;
        }

        if (wallSinceLastBroadcast >= wallInterval && drift >= 0.02f)
        {
            return true;
        }

        return false;
    }

    public static float GameHoursDrift(WorldTime a, WorldTime b)
    {
        float aHours = (a.Day * 24f) + a.Hours24;
        float bHours = (b.Day * 24f) + b.Hours24;
        return Math.Abs(aHours - bHours);
    }

    private static bool TryParseSemicolonField(string report, string prefix, out int value)
    {
        value = 0;
        string[] parts = report.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            if (!parts[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            return int.TryParse(
                parts[i].AsSpan(prefix.Length),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        return false;
    }
}