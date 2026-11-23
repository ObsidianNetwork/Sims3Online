using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// presentation stream (<c>docs/decision-one-simulator.md</c>,
/// <c>docs/engine/presentation-comparison.md</c>). The server TS3's SacsRecorder records every animation
/// call. The server controller pages the calls of drivers that animate Sims on player-occupied lots
/// (SACS_RECORDER dump) and broadcasts them as <see cref="ServerEvent.SimAnimation"/>. Clients replay them
/// on their own instances of those Sims (SACS_REPLAY), which run no interaction logic.
/// A Sim that becomes a target, and every target when a player joins, first gets a history snapshot
/// (SACS_RECORDER snapshot / snapshot-driver): the driver's own calls, so a late joiner plays the path
/// to the current state (a sleeping Sim lies down instead of standing beside the bed).
/// </summary>
public static class PresentationStream
{
    /// <summary>bounded, opt-in presentation polling A/B experiment.</summary>
    public static readonly TimeSpan PumpInterval = ResolvePumpInterval(
        Environment.GetEnvironmentVariable("SIMSONLINE_PRESENTATION_POLL_MS"));

    public static TimeSpan ResolvePumpInterval(string? setting)
    {
        // Missing/invalid values preserve the existing 250ms production default.
        return int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int millis) && millis >= 50 && millis <= 1000
            ? TimeSpan.FromMilliseconds(millis)
            : TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Upper bound on dump pages per pump tick (each page is one bridge round trip).</summary>
    public const int MaxDumpPages = 16;

    /// <summary>Lines per SimAnimation event.</summary>
    public const int MaxLinesPerEvent = 200;

    /// <summary>Hex characters per SACS_REPLAY command (one bridge frame is 4080 bytes).</summary>
    public const int ReplayHexBudget = 3400;

    /// <summary>Asks the recorder for the ring's current end, so a fresh stream starts from now.</summary>
    public const long FromNowCursor = long.MaxValue / 2;

    /// <summary>
    /// The Sims the stream animates: every Sim standing on a lot a player occupies, plus the claimed
    /// Sims themselves (with one simulator they are puppets on their owners' clients too).
    /// </summary>
    public static HashSet<ulong> Targets(IReadOnlyDictionary<ulong, int> claims, IReadOnlyList<SimSnapshot> sims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(sims);
        HashSet<ulong> targets = new(claims.Keys);
        HashSet<ulong> lots = MirroringInterestScope.ComputeInterestLots(claims, sims);
        foreach (SimSnapshot sim in sims)
        {
            if (sim.LotId is ulong lotId && lots.Contains(lotId))
            {
                targets.Add(sim.ObjectId);
            }
        }

        return targets;
    }

    /// <summary>
    /// optional diagnostic-only first/last source event timestamps, recorded
    /// inside TS3 at a measured ~1ms precision. The dump remains wire compatible:
    /// metadata is on the header line; gameplay replay lines are unchanged.
    /// </summary>
    public static bool TryParseSourceClockSamples(string? report,
        out long firstSeq, out long firstUtcTicks,
        out long lastSeq, out long lastUtcTicks)
    {
        firstSeq = firstUtcTicks = lastSeq = lastUtcTicks = 0L;
        if (string.IsNullOrEmpty(report))
        {
            return false;
        }
        string header = report.Split('\n', 2)[0];
        Match m = Regex.Match(header,
            @"source_first_seq=(\d+);source_first_utc_ticks=(\d+);" +
            @"source_last_seq=(\d+);source_last_utc_ticks=(\d+)");
        return m.Success
            && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out firstSeq)
            && long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out firstUtcTicks)
            && long.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out lastSeq)
            && long.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out lastUtcTicks)
            && firstSeq > 0 && lastSeq >= firstSeq && firstUtcTicks > 0 && lastUtcTicks > 0;
    }

    /// <summary>
    /// only these diagnostic samples originate in intercepted
    /// ISacsProxy methods, rather than polling-created RingLine records.
    /// At most four samples per page are carried in the response header.
    /// </summary>
    public static IReadOnlyList<(long Sequence, long UtcTicks)> ParseSacsOriginSamples(string? report)
    {
        List<(long Sequence, long UtcTicks)> samples = new();
        if (string.IsNullOrEmpty(report))
        {
            return samples;
        }
        string header = report.Split('\n', 2)[0];
        Match m = Regex.Match(header, @"(?:^|;)sacs_sources=([0-9@,]+)");
        if (!m.Success)
        {
            return samples;
        }
        foreach (string part in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('@', 2);
            if (pair.Length == 2
                && long.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out long seq)
                && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out long utc)
                && seq > 0 && utc > 0)
            {
                samples.Add((seq, utc));
            }
        }
        return samples;
    }

    /// <summary>"seq=N;more=0|1" then one ring line per row (SACS_RECORDER dump).</summary>
    public static List<string> ParseDump(string? report, out long seq, out bool more)
    {
        seq = -1;
        more = false;
        List<string> lines = new();
        if (string.IsNullOrEmpty(report))
        {
            return lines;
        }

        string[] rows = report.Split('\n');
        Match match = Regex.Match(rows[0], @"seq=(\d+)");
        if (match.Success)
        {
            seq = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        more = rows[0].Contains("more=1", StringComparison.Ordinal);
        for (int i = 1; i < rows.Length; i++)
        {
            if (rows[i].Length > 0)
            {
                lines.Add(rows[i]);
            }
        }

        return lines;
    }

    /// <summary>"drivers=h1,h2|-" (SACS_RECORDER snapshot).</summary>
    public static List<uint> ParseSnapshotDrivers(string? report)
    {
        List<uint> handles = new();
        Match match = Regex.Match(report ?? string.Empty, @"drivers=([\d,]+)");
        if (!match.Success)
        {
            return handles;
        }

        foreach (string part in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint handle))
            {
                handles.Add(handle);
            }
        }

        return handles;
    }

    /// <summary>"lines=N;next=K" then rows (SACS_RECORDER snapshot-driver); next is -1 at the end.</summary>
    public static List<string> ParseSnapshotDriver(string? report, out int next)
    {
        next = -1;
        List<string> lines = new();
        if (string.IsNullOrEmpty(report))
        {
            return lines;
        }

        string[] rows = report.Split('\n');
        Match match = Regex.Match(rows[0], @"next=(-?\d+)");
        if (match.Success)
        {
            next = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        for (int i = 1; i < rows.Length; i++)
        {
            if (rows[i].Length > 0)
            {
                lines.Add(rows[i]);
            }
        }

        return lines;
    }

    /// <summary>
    /// boundaries of actual recorder events in a batch. Synthetic
    /// history/acquire lines have sequence zero and do not identify a new
    /// event; exclude them. This is debug metadata, not gameplay state.
    /// </summary>
    public static (long First, long Last) EventSequenceBounds(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        long first = long.MaxValue;
        long last = -1;
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }
            int split = line.IndexOf('|');
            if (split <= 0 ||
                !long.TryParse(line.AsSpan(0, split), NumberStyles.None,
                    CultureInfo.InvariantCulture, out long seq) || seq <= 0)
            {
                continue;
            }
            first = Math.Min(first, seq);
            last = Math.Max(last, seq);
        }
        return first == long.MaxValue ? (-1, -1) : (first, last);
    }

    /// <summary>The hex payload of one SACS_REPLAY command (UTF-8, as the mod's DecodeText reads it).</summary>
    public static string EncodeReplay(IReadOnlyList<string> lines) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(string.Join("\n", lines)));

    /// <summary>Splits lines into SACS_REPLAY batches that each fit <paramref name="hexBudget"/>.</summary>
    public static List<IReadOnlyList<string>> ReplayBatches(IReadOnlyList<string> lines, int hexBudget = ReplayHexBudget)
    {
        List<IReadOnlyList<string>> batches = new();
        List<string> current = new();
        int bytes = 0;
        foreach (string line in lines)
        {
            int lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (current.Count > 0 && (bytes + lineBytes) * 2 > hexBudget)
            {
                batches.Add(current);
                current = new List<string>();
                bytes = 0;
            }

            current.Add(line);
            bytes += lineBytes;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }
}
