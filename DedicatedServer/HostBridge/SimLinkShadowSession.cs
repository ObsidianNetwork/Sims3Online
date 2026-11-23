using System.Diagnostics;

namespace SimsOnline.DedicatedServer.HostBridge;

/// <summary>
/// Experiment C (internal documentation): the SimLink sidecar as a shadow of the server's presentation
/// stream. Opt-in with SIMSONLINE_SIMLINK_SHADOW=1. Opens the server TS3's sidecar section, runs the handshake
/// (register internal calls, then SACS_RECORDER sidecar-on so the mod mirrors every recorder line into the ring),
/// drains the ring on its own thread as records arrive, and compares with the bridge dump pages. The shadow carries
/// nothing the game or the clients use. Logs "[simlink-shadow] ..." every 10 s.
/// </summary>
public sealed class SimLinkShadowSession : IDisposable
{
    public const string EnvironmentSwitch = "SIMSONLINE_SIMLINK_SHADOW";
    /// <summary>The server ring path: the mod pushes the presentation stream's pages through the ring (lane 6) instead of
    /// answering dumps.</summary>
    public const string ServerRingSwitch = "SIMSONLINE_SIMLINK_SERVER_RING";
    private const ushort PageLane = 6;
    private const int MaxQueuedPages = 4096;

    /// <summary>One pushed page: the dump reply's text, its seq, and when it was published (game QPC) and read here.</summary>
    public readonly record struct Page(string Text, int Seq, long PublishQpc, long ArrivalQpc);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Page> _pages = new();
    private readonly SemaphoreSlim _pageSignal = new(0, int.MaxValue);
    private long _lastPageTicks = DateTime.UtcNow.Ticks;
    private long _pagesReceived;
    private long _pagesDiscarded;
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(10);

    private readonly SimLinkShadowReader _reader;
    private readonly SimLinkShadowComparer _comparer;
    private readonly Thread _drainThread;
    private volatile bool _stopping;
    private volatile bool _drainStopped;
    private DateTime _nextSummary = DateTime.UtcNow + SummaryInterval;

    private SimLinkShadowSession(SimLinkShadowReader reader)
    {
        _reader = reader;
        _comparer = new SimLinkShadowComparer(reader.QpcFrequency);
        _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "simlink-shadow" };
    }

    public SimLinkShadowComparer Comparer => _comparer;

    public static bool Requested => Environment.GetEnvironmentVariable(EnvironmentSwitch) == "1";

    public static bool ServerRingRequested => ServerRingOverride ?? Environment.GetEnvironmentVariable(ServerRingSwitch) == "1";

    /// <summary>Tests: forces <see cref="ServerRingRequested"/>.</summary>
    internal static bool? ServerRingOverride { get; set; }

    /// <summary>Whether the mod mirrors each recorder line on lanes 1/2 (shadow comparison) in this session.</summary>
    public bool Mirroring { get; private set; }

    public long PagesReceived => Interlocked.Read(ref _pagesReceived);

    /// <summary>Whether the drain thread still runs (pushed pages can only arrive while it does).</summary>
    public bool Draining => !_drainStopped && !_stopping;

    /// <summary>When the last pushed page arrived (the mod sends at least one a second while pushing).</summary>
    public DateTime LastPageUtc => new(Interlocked.Read(ref _lastPageTicks), DateTimeKind.Utc);

    /// <summary>Waits until a page is queued or the timeout passes. One wake covers every page queued so far (the
    /// signal counts pages; the extra counts are cleared so a burst doesn't cause empty wakes).</summary>
    public async Task<bool> WaitForPageAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        bool signalled = await _pageSignal.WaitAsync(timeout, cancellationToken);
        while (_pageSignal.CurrentCount > 0 && _pageSignal.Wait(0))
        {
        }

        return signalled;
    }

    public bool TryTakePage(out Page page) => _pages.TryDequeue(out page);

    /// <summary>Moves whatever the ring holds into the page queue now (instead of on the drain thread's next wake).</summary>
    public void Flush() => DrainNow();

    /// <summary>Drops the queued pages (left over from an earlier push); returns how many.</summary>
    public int DiscardQueuedPages()
    {
        DrainNow();
        int dropped = 0;
        while (_pages.TryDequeue(out _))
        {
            dropped++;
        }

        return dropped;
    }

    /// <summary>A session over an already-open ring (tests): no handshake, the drain thread running.</summary>
    internal static SimLinkShadowSession ForTest(SimLinkShadowReader reader, bool mirror)
    {
        var session = new SimLinkShadowSession(reader) { Mirroring = mirror };
        session._drainThread.Start();
        return session;
    }

    /// <summary>Marks the start of pushing (the liveness clock starts now).</summary>
    public void PushStarted() => Interlocked.Exchange(ref _lastPageTicks, DateTime.UtcNow.Ticks);

    /// <summary>One pushed page as the presentation loop consumes it: compared by seq with the mirrored lines when the
    /// mod mirrors, against the page's own arrival time.</summary>
    public void ObservePage(IReadOnlyList<string> lines, long cursor, long arrivalQpc)
    {
        if (Mirroring)
        {
            DrainNow();
            _comparer.ObserveDump(lines, cursor, arrivalQpc);
        }

        MaybeSummarize();
    }

    /// <summary>Starts the shadow for the TS3 process <paramref name="processId"/>, or returns null (with the reason
    /// logged) when the sidecar is absent, registration fails, or the mod refuses. <paramref name="sendRecorderCommand"/>
    /// sends a SACS_RECORDER subcommand over the existing bridge and returns its report.</summary>
    public static async Task<SimLinkShadowSession?> TryStartAsync(
        int? processId,
        string? sandboxieBox,
        Func<string, CancellationToken, Task<string?>> sendRecorderCommand,
        Action<string> log,
        CancellationToken cancellationToken,
        bool mirror = true)
    {
        ArgumentNullException.ThrowIfNull(sendRecorderCommand);
        ArgumentNullException.ThrowIfNull(log);
        if (processId is not int pid)
        {
            log("[simlink-shadow] off: no TS3 process id");
            return null;
        }

        SimLinkShadowReader? reader = SimLinkShadowReader.TryOpen(pid, sandboxieBox, out string reason);
        if (reader == null)
        {
            log($"[simlink-shadow] off: {reason}");
            return null;
        }

        if (!reader.RequestRegistration(TimeSpan.FromSeconds(5)))
        {
            log($"[simlink-shadow] off: registration state={reader.State} error={reader.Error}");
            reader.Dispose();
            return null;
        }

        string report = "mirror=off";
        if (mirror)
        {
            string? reply = await sendRecorderCommand("sidecar-on", cancellationToken);
            if (reply == null || !reply.Contains("sidecar=on", StringComparison.Ordinal))
            {
                log($"[simlink-shadow] off: mod refused sidecar-on: {reply ?? "(no reply)"}");
                reader.Dispose();
                return null;
            }

            report = reply;
        }

        var session = new SimLinkShadowSession(reader);
        session.Mirroring = mirror;
        session._drainThread.Start();
        log($"[simlink-shadow] on: pid={pid} box={sandboxieBox ?? "-"} ring={reader.RingSize} probe_offset={reader.ProbeResult} mod={report}");
        return session;
    }

    /// <summary>One bridge dump page (the lines as returned, the cursor it ended at), stamped on arrival.</summary>
    public void ObserveDump(IReadOnlyList<string> lines, long dumpCursor)
    {
        long arrival = Stopwatch.GetTimestamp();
        if (Mirroring)
        {
            DrainNow();
            _comparer.ObserveDump(lines, dumpCursor, arrival);
        }

        MaybeSummarize();
    }

    private void MaybeSummarize()
    {
        if (DateTime.UtcNow >= _nextSummary)
        {
            _nextSummary = DateTime.UtcNow + SummaryInterval;
            Console.WriteLine("[simlink-shadow] " + _comparer.Summary() +
                $" pages={PagesReceived} pages_discarded={Interlocked.Read(ref _pagesDiscarded)}" +
                $" native_published={_reader.Published} native_dropped={_reader.Dropped} native_contended={_reader.Contended} native_bad_args={_reader.BadArgs}");
        }
    }

    /// <summary>The stream now arrives as pushed pages (server ring path started or re-armed).</summary>
    public void UsePages() => SwitchRoute("page");

    /// <summary>The stream went back to dumps (server ring fallback).</summary>
    public void UseDumps() => SwitchRoute("dump");

    // Logs the previous route's final comparison (when it compared anything), then compares the new route from scratch.
    private void SwitchRoute(string label)
    {
        if (Mirroring)
        {
            DrainNow();
            if (_comparer.DumpLines > 0)
            {
                Console.WriteLine($"[simlink-shadow] final({_comparer.RouteLabel}) " + _comparer.Summary() + $" pages={PagesReceived}");
            }
        }

        _comparer.ResetStatistics(label);
        _nextSummary = DateTime.UtcNow + SummaryInterval;
    }

    private readonly object _drainGate = new();
    private readonly List<SimLinkShadowReader.Record> _buffer = new();

    private void DrainNow()
    {
        lock (_drainGate)
        {
            _buffer.Clear();
            if (_reader.Drain(_buffer) > 0)
            {
                List<SimLinkShadowReader.Record>? mirrored = null;
                foreach (SimLinkShadowReader.Record record in _buffer)
                {
                    if (record.Lane == PageLane)
                    {
                        EnqueuePage(record);
                    }
                    else
                    {
                        (mirrored ??= new List<SimLinkShadowReader.Record>()).Add(record);
                    }
                }

                if (mirrored != null)
                {
                    _comparer.AddShadow(mirrored);
                }
            }
        }
    }

    private void EnqueuePage(SimLinkShadowReader.Record record)
    {
        _pages.Enqueue(new Page(record.Payload, record.Seq, record.Qpc, record.ArrivalQpc));
        Interlocked.Increment(ref _pagesReceived);
        Interlocked.Exchange(ref _lastPageTicks, DateTime.UtcNow.Ticks);
        while (_pages.Count > MaxQueuedPages && _pages.TryDequeue(out _))
        {
            Interlocked.Increment(ref _pagesDiscarded);
        }

        _pageSignal.Release();
    }

    private void DrainLoop()
    {
        while (!_stopping)
        {
            try
            {
                _reader.WaitForData(100);
                DrainNow();
            }
            catch (Exception ex)
            {
                _drainStopped = true;
                Console.WriteLine($"[simlink-shadow] drain stopped: {ex.Message}");
                return;
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _drainThread.Join(TimeSpan.FromSeconds(1));
        _reader.Dispose();
    }
}
