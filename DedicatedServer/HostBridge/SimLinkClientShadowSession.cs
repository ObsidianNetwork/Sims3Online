using System.Diagnostics;
using System.Globalization;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// Experiment C, client side: the SimLink sidecar shadowing the client's replay route. Opt-in with
/// SIMSONLINE_SIMLINK_SHADOW=1 on the client controller. When a SimAnimation event is read off the socket, every line
/// (seq &gt; 0) is also written into the client TS3's inbound ring ("&lt;section&gt;.in") with the controller's
/// QueryPerformanceCounter. The mod drains that ring on the game thread at each bridge pump tick (the sidecar stamps
/// when each line became visible), and when the same line arrives through SACS_REPLAY (the bridge route, which still
/// does the replay) the sidecar compares it by hash and sends a sample back on lane 3 of the outbound ring:
/// "seq|publishQpc|seenQpc|result", qpc = bridge arrival. This session turns the samples into latencies and logs
/// "[simlink-client-shadow] …" every 10 s. Nothing the game shows changes.
///
/// <see cref="SimLinkClientMode.Ring"/> (SIMSONLINE_SIMLINK_CLIENT_RING=1) makes the ring the real path: whole
/// SimAnimation events go into the inbound ring (<see cref="TryRoute"/>, all lines or none) and the mod applies them
/// from it on the game thread; the caller stops sending them over the bridge. The mod reports each read on lane 4
/// ("records|oldestQpc|newestQpc", qpc = read). If the mod stops consuming for 2 s the session marks itself stuck and
/// routes nothing more (the caller falls back to the bridge); lines already in a stuck ring are lost to the client.
/// </summary>
public enum SimLinkClientMode
{
    Shadow,
    Ring,
}

public sealed class SimLinkClientShadowSession : IDisposable
{
    private const ushort SampleLane = 3;
    private const ushort ReadSampleLane = 4;
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(2);
    private const int MaxSamples = 20_000;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);

    private readonly SimLinkShadowReader _outbound;
    private readonly SimLinkShadowReader _inbound;
    private readonly double _ticksPerMs;
    private readonly Thread _drainThread;
    private readonly object _gate = new();
    private readonly List<double> _toSeenMs = new();
    private readonly List<double> _toBridgeMs = new();
    private readonly List<double> _seenAheadMs = new();
    private readonly List<double> _readOldestMs = new();
    private readonly List<double> _readNewestMs = new();
    private readonly Action<string> _log;
    private readonly SimLinkClientMode _mode;
    private long _lastReadPosition = -1;
    private DateTime _readPositionSinceUtc = DateTime.UtcNow;
    private volatile bool _stuck;
    // TryRoute and the stuck claim: an event either goes into the ring before the claim (and is claimed with the rest)
    // or sees the session stuck and takes the bridge. Without it an event published right after the claim is never read.
    private readonly object _routeGate = new();
    private List<string>? _recovered;
    // Re-arm after a stuck: the mod's ring path is switched on again once the backlog has gone over the bridge; the
    // delay doubles (to RingRearmMaxDelay) while the ring gets stuck again within RingHealthy of coming back.
    private Func<string, CancellationToken, Task<string?>>? _sendRecorderCommand;
    private DateTime _ringRearmAtUtc = DateTime.MaxValue;
    private TimeSpan _ringRearmDelay = TimeSpan.Zero;
    private DateTime _ringOnSinceUtc = DateTime.UtcNow;
    private int _rearming;
    private int _cursor;
    private volatile bool _stopping;
    private DateTime _nextSummary = DateTime.UtcNow + SummaryInterval;

    private SimLinkClientShadowSession(SimLinkShadowReader outbound, SimLinkShadowReader inbound, Action<string> log, SimLinkClientMode mode)
    {
        _outbound = outbound;
        _inbound = inbound;
        _log = log;
        _mode = mode;
        _ticksPerMs = outbound.QpcFrequency / 1000.0;
        _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "simlink-client-shadow" };
    }

    public long LinesPublished { get; private set; }
    public long LinesDropped { get; private set; }
    public long Samples { get; private set; }
    public long Matched { get; private set; }
    public long Mismatched { get; private set; }
    public long NotInRing { get; private set; }
    public SimLinkClientMode Mode => _mode;
    public bool Stuck => _stuck;
    public long EventsRouted { get; private set; }
    public long LinesRouted { get; private set; }
    public long EventsFullFallback { get; private set; }

    /// <summary>Lines taken back from the ring when the game stopped reading it (sent over the bridge instead).</summary>
    public long LinesReclaimed { get; private set; }

    /// <summary>Of those, the lines handed to the bridge so far (ahead of the next animation event).</summary>
    public long LinesReclaimedSent { get; private set; }

    /// <summary>Times the ring path came back after a stuck.</summary>
    public long RingRearms { get; private set; }

    internal TimeSpan RingRearmDelay { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan RingRearmMaxDelay { get; set; } = TimeSpan.FromMinutes(5);
    internal TimeSpan RingHealthy { get; set; } = TimeSpan.FromSeconds(60);
    public long ReadBatches { get; private set; }
    public long ReadLines { get; private set; }

    /// <summary>Set by the caller: events sent over the bridge while a bridge-routed SimAnimation was still in flight
    /// (ordering), and while the session was not routing yet. Logged with the summary.</summary>
    public long OrderingWaits;
    public long BridgeInFlight;

    public static async Task<SimLinkClientShadowSession?> TryStartAsync(
        int? processId,
        string? sandboxieBox,
        Func<string, CancellationToken, Task<string?>> sendRecorderCommand,
        Action<string> log,
        CancellationToken cancellationToken,
        SimLinkClientMode mode = SimLinkClientMode.Shadow)
    {
        ArgumentNullException.ThrowIfNull(sendRecorderCommand);
        ArgumentNullException.ThrowIfNull(log);
        if (processId is not int pid)
        {
            log("[simlink-client-shadow] off: no TS3 process id");
            return null;
        }

        SimLinkShadowReader? outbound = SimLinkShadowReader.TryOpen(pid, sandboxieBox, out string reason);
        if (outbound == null)
        {
            log($"[simlink-client-shadow] off: {reason}");
            return null;
        }

        SimLinkShadowReader? inbound = SimLinkShadowReader.TryOpen(pid, sandboxieBox, ".in", out string inReason);
        if (inbound == null)
        {
            log($"[simlink-client-shadow] off: inbound {inReason}");
            outbound.Dispose();
            return null;
        }

        if (!outbound.RequestRegistration(TimeSpan.FromSeconds(5)))
        {
            log($"[simlink-client-shadow] off: registration state={outbound.State} error={outbound.Error}");
            outbound.Dispose();
            inbound.Dispose();
            return null;
        }

        string subcommand = mode == SimLinkClientMode.Ring ? "sidecar-client-ring" : "sidecar-client-on";
        string expected = mode == SimLinkClientMode.Ring ? "ring=on" : "client=on";
        string? report = await sendRecorderCommand(subcommand, cancellationToken);
        if (report == null || !report.Contains(expected, StringComparison.Ordinal))
        {
            log($"[simlink-client-shadow] off: mod refused {subcommand}: {report ?? "(no reply)"}");
            outbound.Dispose();
            inbound.Dispose();
            return null;
        }

        var session = new SimLinkClientShadowSession(outbound, inbound, log, mode);
        session._sendRecorderCommand = sendRecorderCommand;
        session._drainThread.Start();
        log($"[simlink-client-shadow] on: mode={mode} pid={pid} box={sandboxieBox ?? "-"} inbound_ring={inbound.RingSize} mod={report}");
        return session;
    }

    /// <summary>Ring mode: puts a whole SimAnimation event into the inbound ring for the mod to apply. True when the
    /// ring took it (the caller must not send it over the bridge); false when the session is stuck or the ring has no
    /// room for the whole event (the caller sends it over the bridge).</summary>
    public bool TryRoute(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_routeGate)
        {
            if (_mode != SimLinkClientMode.Ring || _stuck)
            {
                return false;
            }

            // The same batches the bridge would send (SACS_REPLAY-sized), each closed by a marker: the game applies
            // exactly those batches, only sooner.
            if (!_inbound.PublishBatches(SimsOnline.DedicatedServer.Mirroring.PresentationStream.ReplayBatches(lines), Stopwatch.GetTimestamp()))
            {
                EventsFullFallback++;
                return false;
            }

            EventsRouted++;
            LinesRouted += lines.Count;
            return true;
        }
    }

    /// <summary>Ring mode, once stuck: the lines taken back from the ring, to go over the bridge ahead of the next
    /// animation event (every later event takes the bridge too, so the order holds). Null when there are none.</summary>
    public List<string>? TakeReclaimedLines()
    {
        List<string>? lines = Interlocked.Exchange(ref _recovered, null);
        if (lines != null)
        {
            LinesReclaimedSent += lines.Count;
        }

        return lines;
    }

    /// <summary>Gives the ring up: routing stops and every line the game hasn't read is taken back (ReclaimedLines).
    /// Returns how many lines were taken.</summary>
    internal int Reclaim()
    {
        var backlog = new List<string>();
        lock (_routeGate)
        {
            _stuck = true;
            foreach (SimLinkShadowReader.Record record in _inbound.ClaimUnread())
            {
                if (record.Lane == SimLinkShadowReader.LaneReplayLine)
                {
                    backlog.Add(record.Payload);
                }
            }
        }

        LinesReclaimed += backlog.Count;
        if (backlog.Count > 0)
        {
            Interlocked.Exchange(ref _recovered, backlog);
        }

        bool healthy = DateTime.UtcNow - _ringOnSinceUtc >= RingHealthy;
        _ringRearmDelay = healthy || _ringRearmDelay <= TimeSpan.Zero
            ? RingRearmDelay
            : TimeSpan.FromTicks(Math.Min(_ringRearmDelay.Ticks * 2, RingRearmMaxDelay.Ticks));
        _ringRearmAtUtc = DateTime.UtcNow + _ringRearmDelay;
        return backlog.Count;
    }

    /// <summary>Ring mode, stuck: once the re-arm delay has passed, switch the mod's ring path on again and route
    /// through the ring. A reclaimed backlog no animation event has carried to the bridge yet goes into the ring first:
    /// with no event since the stuck, nothing newer is on the bridge either. The caller's ordering rule (no
    /// bridge-routed animation still in flight) keeps the order across the switch. True when the ring is back.</summary>
    internal async Task<bool> MaybeRearmAsync(CancellationToken cancellationToken)
    {
        if (!_stuck || _sendRecorderCommand == null || DateTime.UtcNow < _ringRearmAtUtc
            || Interlocked.CompareExchange(ref _rearming, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            string? reply = await _sendRecorderCommand("sidecar-client-ring", cancellationToken);
            if (reply == null || !reply.Contains("ring=on", StringComparison.Ordinal))
            {
                _ringRearmDelay = TimeSpan.FromTicks(Math.Min(Math.Max(_ringRearmDelay.Ticks, RingRearmDelay.Ticks) * 2, RingRearmMaxDelay.Ticks));
                _ringRearmAtUtc = DateTime.UtcNow + _ringRearmDelay;
                _log($"[simlink-client-shadow] re-arm refused; next try in {_ringRearmDelay.TotalSeconds:F0} s: {reply ?? "(no reply)"}");
                return false;
            }

            int carried = 0;
            lock (_routeGate)
            {
                List<string>? backlog = Interlocked.Exchange(ref _recovered, null);
                if (backlog != null)
                {
                    if (!_inbound.PublishBatches(SimsOnline.DedicatedServer.Mirroring.PresentationStream.ReplayBatches(backlog), Stopwatch.GetTimestamp()))
                    {
                        Interlocked.Exchange(ref _recovered, backlog); // the bridge takes it with the next event
                        _ringRearmAtUtc = DateTime.UtcNow + RingRearmDelay;
                        _log("[simlink-client-shadow] re-arm put off: the backlog does not fit the ring");
                        return false;
                    }

                    carried = backlog.Count;
                    LinesReclaimedSent += carried;
                }

                _lastReadPosition = -1;
                _readPositionSinceUtc = DateTime.UtcNow;
                _ringOnSinceUtc = DateTime.UtcNow;
                _ringRearmAtUtc = DateTime.MaxValue;
                _stuck = false;
            }

            RingRearms++;
            _log($"[simlink-client-shadow] re-arm: the ring is the path again (re-arms={RingRearms}, backlog carried in the ring={carried})");
            return true;
        }
        catch (Exception ex)
        {
            _ringRearmAtUtc = DateTime.UtcNow + RingRearmDelay;
            _log($"[simlink-client-shadow] re-arm failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _rearming, 0);
        }
    }

    /// <summary>A ring-mode session over already-open rings (tests): no handshake, no drain thread.</summary>
    internal static SimLinkClientShadowSession ForTest(SimLinkShadowReader outbound, SimLinkShadowReader inbound, Action<string> log,
        Func<string, CancellationToken, Task<string?>>? sendRecorderCommand = null) =>
        new(outbound, inbound, log, SimLinkClientMode.Ring) { _sendRecorderCommand = sendRecorderCommand };

    /// <summary>The lines of one SimAnimation event, as read off the socket.</summary>
    public void ObserveIncoming(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        long now = Stopwatch.GetTimestamp();
        foreach (string line in lines)
        {
            int split = line.IndexOf('|');
            if (split <= 0 || !int.TryParse(line.AsSpan(0, split), NumberStyles.None, CultureInfo.InvariantCulture, out int seq) || seq <= 0)
            {
                continue;
            }

            if (_inbound.Publish(1, seq, line, now))
            {
                LinesPublished++;
            }
            else
            {
                LinesDropped++;
            }
        }
    }

    private void DrainLoop()
    {
        var records = new List<SimLinkShadowReader.Record>();
        while (!_stopping)
        {
            try
            {
                _outbound.WaitForData(100);
                records.Clear();
                _outbound.Drain(records);
                foreach (SimLinkShadowReader.Record record in records)
                {
                    if (record.Lane == SampleLane)
                    {
                        AddSample(record);
                    }
                    else if (record.Lane == ReadSampleLane)
                    {
                        AddReadSample(record);
                    }
                }

                if (_mode == SimLinkClientMode.Ring)
                {
                    CheckStuck();
                    if (_stuck && DateTime.UtcNow >= _ringRearmAtUtc)
                    {
                        _ = MaybeRearmAsync(CancellationToken.None);
                    }
                }

                if (DateTime.UtcNow >= _nextSummary)
                {
                    _nextSummary = DateTime.UtcNow + SummaryInterval;
                    _log("[simlink-client-shadow] " + Summary());
                }
            }
            catch (Exception ex)
            {
                _log($"[simlink-client-shadow] drain stopped: {ex.Message}");
                return;
            }
        }
    }

    // Ring mode: the mod must keep consuming. Behind and not moving for StuckAfter: stop routing (bridge fallback).
    private void CheckStuck()
    {
        long readPosition = _inbound.ReadPosition;
        long writePosition = _inbound.WritePosition;
        if (readPosition != _lastReadPosition || readPosition >= writePosition)
        {
            _lastReadPosition = readPosition;
            _readPositionSinceUtc = DateTime.UtcNow;
            return;
        }

        if (!_stuck && DateTime.UtcNow - _readPositionSinceUtc > StuckAfter)
        {
            int reclaimed = Reclaim();
            _log($"[simlink-client-shadow] STUCK: the mod stopped consuming (read={readPosition} write={writePosition}); routing falls back to the bridge; " +
                $"reclaimed {reclaimed} unread line(s), sent over the bridge ahead of the next animation event");
        }
    }

    // Ring mode: "records|oldestQpc|newestQpc"; record.Qpc = when the game thread read them.
    private void AddReadSample(SimLinkShadowReader.Record record)
    {
        string[] f = record.Payload.Split('|');
        if (f.Length < 3
            || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long oldest)
            || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long newest))
        {
            return;
        }

        lock (_gate)
        {
            ReadBatches++;
            ReadLines += count;
            Add(_readOldestMs, (record.Qpc - oldest) / _ticksPerMs);
            Add(_readNewestMs, (record.Qpc - newest) / _ticksPerMs);
            _cursor++;
        }
    }

    // "seq|publishQpc|seenQpc|result"; record.Qpc = bridge arrival on the game thread.
    private void AddSample(SimLinkShadowReader.Record record)
    {
        string[] f = record.Payload.Split('|');
        if (f.Length < 4
            || !long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long published)
            || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seen)
            || !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            return;
        }

        lock (_gate)
        {
            Samples++;
            if (result == 2)
            {
                NotInRing++;
                return;
            }

            if (result == 1)
            {
                Matched++;
            }
            else
            {
                Mismatched++;
            }

            Add(_toSeenMs, (seen - published) / _ticksPerMs);
            Add(_toBridgeMs, (record.Qpc - published) / _ticksPerMs);
            Add(_seenAheadMs, (record.Qpc - seen) / _ticksPerMs);
            _cursor++;
        }
    }

    private void Add(List<double> samples, double value)
    {
        if (samples.Count < MaxSamples)
        {
            samples.Add(value);
        }
        else
        {
            samples[_cursor % MaxSamples] = value;
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            if (_mode == SimLinkClientMode.Ring)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"mode=ring events_routed={EventsRouted} lines_routed={LinesRouted} full_fallback={EventsFullFallback} ordering_waits={Interlocked.Read(ref OrderingWaits)} " +
                    $"bridge_in_flight={Interlocked.Read(ref BridgeInFlight)} stuck={_stuck} reclaimed={LinesReclaimed} reclaimed_sent={LinesReclaimedSent} rearms={RingRearms} read_batches={ReadBatches} read_lines={ReadLines} " +
                    $"controller_to_game_read_oldest_ms={Percentiles(_readOldestMs)} controller_to_game_read_newest_ms={Percentiles(_readNewestMs)} " +
                    $"native_bad_args={_outbound.BadArgs} inbound_dropped={_inbound.Dropped}");
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"published={LinesPublished} dropped={LinesDropped} samples={Samples} matched={Matched} mismatched={Mismatched} not_in_ring={NotInRing} " +
                $"controller_to_game_ring_ms={Percentiles(_toSeenMs)} controller_to_game_bridge_ms={Percentiles(_toBridgeMs)} " +
                $"ring_ahead_of_bridge_ms={Percentiles(_seenAheadMs)} native_dropped={_outbound.Dropped} native_bad_args={_outbound.BadArgs} inbound_dropped={_inbound.Dropped}");
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

    public void Dispose()
    {
        _stopping = true;
        if (_drainThread.IsAlive)
        {
            _drainThread.Join(TimeSpan.FromSeconds(1));
        }

        _outbound.Dispose();
        _inbound.Dispose();
    }
}
