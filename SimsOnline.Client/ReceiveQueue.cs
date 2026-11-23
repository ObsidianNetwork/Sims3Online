using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace SimsOnline.Client;

/// <summary>How far the handlers are behind the socket: the lines queued and how long the oldest has waited.</summary>
internal readonly record struct ReceiveLag(bool Behind, int Depth, double OldestSeconds, int MaxDepth, double MaxOldestSeconds);

/// <summary>
/// the in-order queue between the client's socket reader and its handlers (the join drain, then the
/// MirrorCoordinator). The reader only ever writes here, so the socket is drained whatever a handler waits on. Each
/// line keeps its arrival time, so <see cref="WatchAsync"/> can tell a stalled handler from an idle server.
/// Source-linked into DedicatedServer.Tests.
/// </summary>
internal sealed class ReceiveQueue
{
    // Several handlers read in turn, never at once; SingleReader stays off so Count and TryPeek are available.
    private readonly Channel<(string Line, long At)> _channel =
        Channel.CreateUnbounded<(string Line, long At)>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    public ReceiveQueue()
    {
        Reader = new LineReader(_channel.Reader);
    }

    /// <summary>The lines in arrival order, for the handlers.</summary>
    public ChannelReader<string> Reader { get; }

    public int Depth => _channel.Reader.Count;

    public TimeSpan OldestAge => _channel.Reader.TryPeek(out (string Line, long At) oldest)
        ? Stopwatch.GetElapsedTime(oldest.At)
        : TimeSpan.Zero;

    public bool TryWrite(string line) => _channel.Writer.TryWrite((line, Stopwatch.GetTimestamp()));

    /// <summary>No more lines; with an error, a handler's next read throws it once the queued lines are read.</summary>
    public bool TryComplete(Exception? error = null) => _channel.Writer.TryComplete(error);

    /// <summary>
    /// Until cancelled, check every <paramref name="every"/> whether the handlers are behind: <paramref name="maxDepth"/>
    /// lines queued or the oldest waiting <paramref name="maxAge"/>. Reports once when that starts, again every
    /// <paramref name="repeat"/> while it lasts, and once when the queue has caught up (with the worst seen).
    /// </summary>
    public async Task WatchAsync(int maxDepth, TimeSpan maxAge, Action<ReceiveLag> report, CancellationToken cancellationToken,
        TimeSpan? every = null, TimeSpan? repeat = null)
    {
        TimeSpan again = repeat ?? TimeSpan.FromSeconds(10);
        using PeriodicTimer timer = new(every ?? TimeSpan.FromSeconds(1));
        bool behind = false;
        long saidAt = 0;
        int worstDepth = 0;
        TimeSpan worstAge = TimeSpan.Zero;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                int depth = Depth;
                TimeSpan age = OldestAge;
                if (depth >= maxDepth || age >= maxAge)
                {
                    worstDepth = Math.Max(worstDepth, depth);
                    worstAge = age > worstAge ? age : worstAge;
                    if (!behind || Stopwatch.GetElapsedTime(saidAt) >= again)
                    {
                        report(new ReceiveLag(true, depth, Seconds(age), worstDepth, Seconds(worstAge)));
                        saidAt = Stopwatch.GetTimestamp();
                    }

                    behind = true;
                }
                else if (behind)
                {
                    report(new ReceiveLag(false, depth, Seconds(age), worstDepth, Seconds(worstAge)));
                    behind = false;
                    worstDepth = 0;
                    worstAge = TimeSpan.Zero;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static double Seconds(TimeSpan span) => Math.Round(span.TotalSeconds, 1);

    /// <summary>The queue's lines without their arrival times (ReadAsync and ReadAllAsync come from these).</summary>
    private sealed class LineReader(ChannelReader<(string Line, long At)> inner) : ChannelReader<string>
    {
        public override Task Completion => inner.Completion;

        public override bool CanCount => inner.CanCount;

        public override int Count => inner.Count;

        public override bool CanPeek => inner.CanPeek;

        public override bool TryPeek([MaybeNullWhen(false)] out string item)
        {
            bool found = inner.TryPeek(out (string Line, long At) entry);
            item = found ? entry.Line : null;
            return found;
        }

        public override bool TryRead([MaybeNullWhen(false)] out string item)
        {
            bool found = inner.TryRead(out (string Line, long At) entry);
            item = found ? entry.Line : null;
            return found;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToReadAsync(cancellationToken);
    }
}
