using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SimsOnline.DedicatedServer.Network;

/// <summary>
/// G14: the timing of one join, written as one block when it is done, so it is cheap to leave on. Each step
/// records its start, its milliseconds and its item counts: the session's command gate, snapshot capture and scoping,
/// and every send the join queues on the outbound pump, with the pump's state at that moment (what is in flight, what
/// is queued, and for each recipient what is pending for it and the oldest of it). Every event encoded for the join is
/// tallied by type, with its bytes. The connection begins one for a JoinSession and for the first AckSnapshot after
/// it, and the session one for a joiner's presentation history; code on that async flow finds it through
/// <see cref="Current"/>. Every line of the block starts with "[join] player=N".
/// </summary>
internal sealed class JoinTrace
{
    private static readonly AsyncLocal<JoinTrace?> s_current = new();

    private readonly object _lock = new();
    private readonly string _subject;
    private readonly string _title;
    private readonly long _started;
    private readonly string _utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private readonly List<(long At, Func<string> Render)> _entries = new();
    private readonly SortedDictionary<string, (int Count, long Bytes)> _replies = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, (int Count, long Bytes)> _broadcasts = new(StringComparer.Ordinal);
    private volatile bool _emitted;

    /// <param name="subject">Who the block is about ("player=57"); every line starts with it.</param>
    /// <param name="title">What is traced ("JoinSession remote=...", "AckSnapshot tick=12", "presentation-history").</param>
    /// <param name="owner">The joining connection: sends to it are labelled "self".</param>
    /// <param name="started">The trace's zero, when its first step began before the trace was made.</param>
    public JoinTrace(string subject, string title, object? owner = null, long? started = null)
    {
        _subject = subject;
        _title = title;
        Owner = owner;
        _started = started ?? Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The trace on this async flow, or null (the common case: nothing is traced). A written trace is no longer
    /// current, so work the join started that outlives it (a superseded connection's close) records nothing.
    /// </summary>
    public static JoinTrace? Current => s_current.Value is { _emitted: false } trace ? trace : null;

    public object? Owner { get; }

    /// <summary>How it ended ("accepted", "rejected ...", "error ..."), printed in the first line.</summary>
    public string? Outcome { get; set; }

    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Makes <paramref name="trace"/> current until the scope is disposed.</summary>
    public static Scope Enter(JoinTrace trace)
    {
        Scope scope = new(s_current.Value);
        s_current.Value = trace;
        return scope;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly JoinTrace? _previous;
        private readonly bool _entered;

        internal Scope(JoinTrace? previous)
        {
            _previous = previous;
            _entered = true;
        }

        public void Dispose()
        {
            if (_entered)
            {
                s_current.Value = _previous;
            }
        }
    }

    /// <summary>A step that began at <paramref name="since"/> and ends now.</summary>
    public void Step(string name, long since, string? detail = null) => Step(name, since, Stopwatch.GetTimestamp(), detail);

    public void Step(string name, long since, long until, string? detail)
    {
        string text = Invariant($"{name} {Ms(since, until):F1} ms") + (detail == null ? string.Empty : " " + detail);
        lock (_lock)
        {
            _entries.Add((since, () => text));
        }
    }

    /// <summary>One event encoded for the join: its type and line (with the newline the write adds).</summary>
    public void Encoded(string type, string line, bool broadcast)
    {
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        lock (_lock)
        {
            SortedDictionary<string, (int Count, long Bytes)> tally = broadcast ? _broadcasts : _replies;
            tally.TryGetValue(type, out (int Count, long Bytes) sum);
            tally[type] = (sum.Count + 1, sum.Bytes + bytes);
        }
    }

    /// <summary>A send queued on the outbound pump; the delivery fills in its wait, write times and results.</summary>
    public Send AddSend(string signal, string pump, string[] recipients, string[] pendingFor, long[] bytes)
    {
        Send send = new(signal, pump, recipients, pendingFor, bytes);
        lock (_lock)
        {
            _entries.Add((send.Queued, send.Render));
        }
        return send;
    }

    /// <summary>Writes the block: a header, one line per step in start order, and the events by type.</summary>
    public void Emit(Action<string> log)
    {
        StringBuilder text = new();
        lock (_lock)
        {
            text.Append(Invariant(
                $"[join] {_subject} {_title} utc={_utc} total={Ms(_started, Stopwatch.GetTimestamp()):F1} ms outcome={Outcome ?? "done"}"));
            foreach ((long at, Func<string> render) in _entries.OrderBy(static entry => entry.At))
            {
                text.Append('\n').Append(Invariant($"[join] {_subject} +{Ms(_started, at):F1} {render()}"));
            }
            text.Append('\n').Append(Invariant($"[join] {_subject} events reply: {Tallies(_replies)}; broadcast: {Tallies(_broadcasts)}"));
        }
        _emitted = true;
        log(text.ToString());
    }

    private static string Tallies(SortedDictionary<string, (int Count, long Bytes)> tally) =>
        tally.Count == 0
            ? "none"
            : string.Join(", ", tally.Select(static pair => Invariant($"{pair.Key} x{pair.Value.Count} {pair.Value.Bytes} B")));

    private static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>One queued send: the pump's state when it was queued, its wait, and each recipient's write.</summary>
    internal sealed class Send
    {
        private readonly string _signal;
        private readonly string _pump;
        private readonly string[] _recipients;
        private readonly string[] _pendingFor;
        private readonly long[] _bytes;
        private readonly double[] _writeMs;
        private readonly string?[] _results;
        private long _deliveryStarted;
        private long _deliveryEnded;
        private string? _refused;

        internal Send(string signal, string pump, string[] recipients, string[] pendingFor, long[] bytes)
        {
            _signal = signal;
            _pump = pump;
            _recipients = recipients;
            _pendingFor = pendingFor;
            _bytes = bytes;
            _writeMs = new double[recipients.Length];
            _results = new string?[recipients.Length];
        }

        public long Queued { get; } = Stopwatch.GetTimestamp();

        public void Started() => Volatile.Write(ref _deliveryStarted, Stopwatch.GetTimestamp());

        public void Ended() => Volatile.Write(ref _deliveryEnded, Stopwatch.GetTimestamp());

        public void Refused(string why) => _refused = why;

        /// <summary>One recipient's write: began at <paramref name="since"/>, ended now; result "ok", "failed" or "broken".</summary>
        public void Wrote(int index, long since, string result)
        {
            _writeMs[index] = Ms(since, Stopwatch.GetTimestamp());
            Volatile.Write(ref _results[index], result);
        }

        internal string Render()
        {
            long started = Volatile.Read(ref _deliveryStarted);
            long ended = Volatile.Read(ref _deliveryEnded);
            StringBuilder text = new(Invariant($"send {_signal} to={_recipients.Length}"));
            if (_refused != null)
            {
                text.Append(" refused=").Append(_refused);
            }
            else if (started == 0)
            {
                text.Append(Invariant($" wait>{Ms(Queued, Stopwatch.GetTimestamp()):F1} ms (not started)"));
            }
            else
            {
                text.Append(Invariant($" wait={Ms(Queued, started):F1} ms"));
                text.Append(ended == 0 ? " deliver=unfinished" : Invariant($" deliver={Ms(started, ended):F1} ms"));
            }

            text.Append(" | pump ").Append(_pump);
            for (int i = 0; i < _recipients.Length; i++)
            {
                string? result = Volatile.Read(ref _results[i]);
                text.Append(" | ").Append(_recipients[i]).Append(' ').Append(_pendingFor[i])
                    .Append(Invariant($" {_bytes[i]} B"))
                    .Append(result == null ? " unwritten" : Invariant($" write={_writeMs[i]:F1} ms {result}"));
            }
            return text.ToString();
        }
    }
}
