namespace SimsOnline.Client;

/// <summary>
/// One-shot timers, one per key, whose callbacks run on the <see cref="MirrorCoordinator"/> drain loop instead of on the
/// timer's thread, so the state the dispatch path owns keeps a single owner. The timer's thread only waits and posts.
/// Call <see cref="Schedule"/>, <see cref="Cancel"/> and <see cref="IsPending"/> from the drain loop, or before it
/// starts (the client's join phase dispatches inline, on the same flow).
/// </summary>
public sealed class CoordinatorTimers<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, CancellationTokenSource> _pending = new();
    private readonly Func<Func<CancellationToken, Task>, bool> _post;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="post">Queues work on the drain loop (<see cref="MirrorCoordinator.Post"/>).</param>
    /// <param name="delay">The wait; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test passes its own.</param>
    public CoordinatorTimers(Func<Func<CancellationToken, Task>, bool> post, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _post = post;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Run <paramref name="elapsed"/> on the drain loop <paramref name="after"/> from now. Replaces a timer
    /// already set for <paramref name="key"/>.</summary>
    public void Schedule(TKey key, TimeSpan after, Func<CancellationToken, Task> elapsed, CancellationToken cancellationToken)
    {
        Cancel(key);
        CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pending[key] = timer;
        _ = WaitThenPostAsync(key, timer, after, elapsed);
    }

    /// <summary>Stop the timer for <paramref name="key"/>, also when it elapsed and its work waits in the channel: the
    /// callback will not run. False when no timer was set.</summary>
    public bool Cancel(TKey key)
    {
        if (!_pending.Remove(key, out CancellationTokenSource? timer))
        {
            return false;
        }

        timer.Cancel();
        timer.Dispose();
        return true;
    }

    public bool IsPending(TKey key) => _pending.ContainsKey(key);

    // The wait starts on the caller's flow; after it, on a timer thread, this touches nothing but the post.
    private async Task WaitThenPostAsync(TKey key, CancellationTokenSource timer, TimeSpan after, Func<CancellationToken, Task> elapsed)
    {
        try
        {
            await _delay(after, timer.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _post(cancellationToken => FireAsync(key, timer, elapsed, cancellationToken));
    }

    // On the drain loop: runs only while this timer is still the key's own (not cancelled or replaced since it elapsed).
    private Task FireAsync(TKey key, CancellationTokenSource timer, Func<CancellationToken, Task> elapsed, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(key, out CancellationTokenSource? current) || !ReferenceEquals(current, timer))
        {
            return Task.CompletedTask;
        }

        _pending.Remove(key);
        timer.Dispose();
        return elapsed(cancellationToken);
    }
}
