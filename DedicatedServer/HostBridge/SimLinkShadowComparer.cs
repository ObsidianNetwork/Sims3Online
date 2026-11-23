using System.Globalization;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// SimLink shadow comparison: every recorder line reaches the controller twice, through the bridge dump
/// ("seq|line") and through the sidecar ring (seq + line + the game's QueryPerformanceCounter at publish). For each
/// dump line with seq &gt; 0 this checks the sidecar delivered the same line, and measures both paths against the
/// same source clock: source→shadow (ring) and source→dump (bridge). Latencies count lane 1 only (real SACS
/// callbacks); lane-2 lines are generated while the dump runs, so their source time says nothing about waiting.
/// Thread-safe.
/// </summary>
public sealed class SimLinkShadowComparer
{
    private const int MaxPending = 200_000;
    private const int MaxSamples = 20_000;

    private readonly object _gate = new();
    private readonly Dictionary<int, SimLinkShadowReader.Record> _pending = new();
    private readonly double _ticksPerMs;
    private readonly List<double> _sourceToShadowMs = new();
    private readonly List<double> _sourceToDumpMs = new();
    private int _sampleCursor;

    public SimLinkShadowComparer(long qpcFrequency)
    {
        _ticksPerMs = qpcFrequency / 1000.0;
    }

    /// <summary>What the second route is called in the summary: "dump" (bridge dumps) or "page" (pushed pages).</summary>
    public string RouteLabel { get; set; } = "dump";

    public long ShadowRecords { get; private set; }
    public long DumpLines { get; private set; }
    public long Matched { get; private set; }
    public long PayloadMismatches { get; private set; }
    public long MissingInShadow { get; private set; }
    public long EvictedPending { get; private set; }
    public long MatchedPolled { get; private set; }
    public string LastMismatch { get; private set; } = "-";

    public void AddShadow(IEnumerable<SimLinkShadowReader.Record> records)
    {
        lock (_gate)
        {
            foreach (SimLinkShadowReader.Record record in records)
            {
                ShadowRecords++;
                _pending[record.Seq] = record;
            }

            if (_pending.Count > MaxPending)
            {
                int cutoff = _pending.Keys.Max() - MaxPending / 2;
                foreach (int seq in _pending.Keys.Where(k => k < cutoff).ToList())
                {
                    _pending.Remove(seq);
                    EvictedPending++;
                }
            }
        }
    }

    /// <summary>One bridge dump page: its "seq|line" lines (synthetic seq-0 lines are ignored), the dump cursor it
    /// returned (shadow records at or below it that the dump didn't carry belong to drivers outside the targets and
    /// are dropped), and when it arrived (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</summary>
    public void ObserveDump(IReadOnlyList<string> lines, long dumpCursor, long arrivalQpc)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            foreach (string line in lines)
            {
                int split = line.IndexOf('|');
                if (split <= 0 || !int.TryParse(line.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out int seq) || seq <= 0)
                {
                    continue;
                }

                DumpLines++;
                if (!_pending.Remove(seq, out SimLinkShadowReader.Record shadow))
                {
                    MissingInShadow++;
                    continue;
                }

                Matched++;
                if (!line.AsSpan(split + 1).SequenceEqual(shadow.Payload))
                {
                    PayloadMismatches++;
                    LastMismatch = seq.ToString(CultureInfo.InvariantCulture);
                }

                if (shadow.Lane != 1)
                {
                    MatchedPolled++;
                    continue;
                }

                AddSample(_sourceToShadowMs, (shadow.ArrivalQpc - shadow.Qpc) / _ticksPerMs);
                AddSample(_sourceToDumpMs, (arrivalQpc - shadow.Qpc) / _ticksPerMs);
                _sampleCursor++;
            }

            if (dumpCursor > 0)
            {
                foreach (int seq in _pending.Keys.Where(k => k <= dumpCursor).ToList())
                {
                    _pending.Remove(seq);
                }
            }
        }
    }

    /// <summary>Starts the counters and latency samples over under a new route label (the pending mirrored lines stay,
    /// so the next route's first pages still find them).</summary>
    public void ResetStatistics(string routeLabel)
    {
        lock (_gate)
        {
            RouteLabel = routeLabel;
            ShadowRecords = 0;
            DumpLines = 0;
            Matched = 0;
            PayloadMismatches = 0;
            MissingInShadow = 0;
            EvictedPending = 0;
            MatchedPolled = 0;
            LastMismatch = "-";
            _sourceToShadowMs.Clear();
            _sourceToDumpMs.Clear();
            _sampleCursor = 0;
        }
    }

    private void AddSample(List<double> samples, double value)
    {
        if (samples.Count < MaxSamples)
        {
            samples.Add(value);
        }
        else
        {
            samples[_sampleCursor % MaxSamples] = value;
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"shadow={ShadowRecords} dump={DumpLines} matched={Matched} matched_polled={MatchedPolled} payload_mismatch={PayloadMismatches} missing={MissingInShadow} " +
                $"evicted={EvictedPending} last_mismatch_seq={LastMismatch} " +
                $"source_to_shadow_ms={Percentiles(_sourceToShadowMs)} source_to_{RouteLabel}_ms={Percentiles(_sourceToDumpMs)}");
        }
    }

    private static string Percentiles(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return "-";
        }

        double[] sorted = samples.ToArray();
        Array.Sort(sorted);
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * (sorted.Length - 1) + 0.5))];
        return string.Create(CultureInfo.InvariantCulture, $"p50:{At(0.5):F3}/p95:{At(0.95):F3}/p99:{At(0.99):F3}/n:{sorted.Length}");
    }
}
