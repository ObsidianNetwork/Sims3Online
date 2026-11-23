using System.Threading.Channels;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>Client timers run their work on the coordinator's drain loop, the one owner of the dispatch state, never on the timer's thread.</summary>
internal static partial class Program
{
    private static readonly TimeSpan CoordinatorTestWait = TimeSpan.FromSeconds(10);

    [Test("MirrorCoordinator.TimerFlushWaitsForTheDrainLoop")]
    private static void MirrorCoordinatorTimerFlushWaitsForTheDrainLoop() => RunTimerFlushWaitsForTheDrainLoopAsync().GetAwaiter().GetResult();

    private static async Task RunTimerFlushWaitsForTheDrainLoopAsync()
    {
        Channel<string> channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        MirrorCoordinator coordinator = new(new CoordinatorConfig(SelfPlayerId: 1, MaxFoldPerCycle: 8, ResyncCooldown: TimeSpan.FromSeconds(60)));
        TaskCompletionSource timerElapses = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource dispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool dispatching = false;
        bool flushOverlappedDispatch = false;

        CoordinatorTimers<ulong> timers = new(
            work =>
            {
                bool queued = coordinator.Post(channel.Writer.TryWrite, work);
                posted.TrySetResult();
                return queued;
            },
            (after, token) => timerElapses.Task.WaitAsync(token));

        async Task<bool> Dispatch(MirrorEngine? engine, string line, bool json, CancellationToken token, Func<ClientCommand, Task> send)
        {
            dispatching = true;
            dispatchStarted.TrySetResult();
            await releaseDispatch.Task;
            dispatching = false;
            return false;
        }

        // Set before the loop starts (this thread owns the state until then), like the client's join phase.
        channel.Writer.TryWrite(MakeSnapshotDeltaLine(1, 42uL, 1f));
        timers.Schedule(7uL, TimeSpan.FromSeconds(3), token =>
        {
            flushOverlappedDispatch = dispatching;
            flushed.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        Task run = coordinator.RunAsync(channel.Reader, null, false, CancellationToken.None, _ => Task.CompletedTask, Dispatch);
        await dispatchStarted.Task.WaitAsync(CoordinatorTestWait);

        // The timer elapses on a thread-pool thread while the line is still being dispatched.
        timerElapses.SetResult();
        Task first = await Task.WhenAny(posted.Task, flushed.Task).WaitAsync(CoordinatorTestWait);
        Assert(first == posted.Task && !flushed.Task.IsCompleted,
            "An elapsed timer must post its flush to the drain loop, not run it on the timer's thread.");

        releaseDispatch.SetResult();
        await flushed.Task.WaitAsync(CoordinatorTestWait);
        Assert(!flushOverlappedDispatch, "The flush must run after the dispatch in progress, never during it.");
        Assert(!timers.IsPending(7uL), "A timer that fired is no longer pending.");

        channel.Writer.Complete();
        await run.WaitAsync(CoordinatorTestWait);
    }

    [Test("MirrorCoordinator.PostedWorkRunsInLineOrder")]
    private static void MirrorCoordinatorPostedWorkRunsInLineOrder() => RunPostedWorkRunsInLineOrderAsync().GetAwaiter().GetResult();

    private static async Task RunPostedWorkRunsInLineOrderAsync()
    {
        Channel<string> channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        MirrorCoordinator coordinator = new(new CoordinatorConfig(SelfPlayerId: 1, MaxFoldPerCycle: 8, ResyncCooldown: TimeSpan.FromSeconds(60)));
        List<string> order = new();
        List<int> lineCounts = new();

        Task<bool> Dispatch(MirrorEngine? engine, string line, bool json, CancellationToken token, Func<ClientCommand, Task> send)
        {
            order.Add(PublicProtocolCodec.DecodeEvent(line) is ServerEvent.SnapshotDelta delta ? "tick " + delta.Snapshot.Tick : "other");
            return Task.FromResult(false);
        }

        // Work posted between two deltas: like any other event, the delta before it is dispatched first (not merged with
        // the one after).
        channel.Writer.TryWrite(MakeSnapshotDeltaLine(1, 42uL, 1f));
        Assert(coordinator.Post(channel.Writer.TryWrite, _ =>
        {
            order.Add("work");
            return Task.CompletedTask;
        }), "Posting to an open channel should succeed.");
        channel.Writer.TryWrite(MakeSnapshotDeltaLine(2, 42uL, 2f));
        channel.Writer.Complete();

        await coordinator.RunAsync(channel.Reader, null, false, CancellationToken.None, _ => Task.CompletedTask, Dispatch,
            afterLine: (line, count) =>
            {
                lineCounts.Add(count);
                return Task.FromResult(false);
            }).WaitAsync(CoordinatorTestWait);

        Assert(string.Join(",", order) == "tick 1,work,tick 2", "Posted work should run after the lines before it and before the lines after it, got " + string.Join(",", order));
        Assert(string.Join(",", lineCounts) == "1,2", "Posted work is not a line: afterLine sees only the two lines, got " + string.Join(",", lineCounts));
        Assert(!coordinator.Post(channel.Writer.TryWrite, _ => Task.CompletedTask), "Posting to a closed channel should report that the work will not run.");
    }

    [Test("MirrorCoordinator.JoinDrainRunsPostedWorkBetweenLines")]
    private static void MirrorCoordinatorJoinDrainRunsPostedWorkBetweenLines() => RunJoinDrainRunsPostedWorkBetweenLinesAsync().GetAwaiter().GetResult();

    private static async Task RunJoinDrainRunsPostedWorkBetweenLinesAsync()
    {
        // The client's join drain reads its receive queue before the drain loop does, and a timer can post meanwhile.
        ReceiveQueue queue = new();
        MirrorCoordinator coordinator = new(new CoordinatorConfig(SelfPlayerId: 1, MaxFoldPerCycle: 8, ResyncCooldown: TimeSpan.FromSeconds(60)));
        List<string> order = new();
        queue.TryWrite("first");
        Assert(coordinator.Post(queue.TryWrite, _ =>
        {
            order.Add("work");
            return Task.CompletedTask;
        }), "Posting to the receive queue should succeed.");
        queue.TryWrite("second");

        for (int i = 0; i < 2; i++)
        {
            order.Add(await coordinator.ReadLineAsync(queue.Reader, false, CancellationToken.None).WaitAsync(CoordinatorTestWait) ?? "(end)");
        }

        Assert(string.Join(",", order) == "first,work,second",
            "The join drain should get only lines, with posted work run between them in queue order, got " + string.Join(",", order));

        queue.TryComplete();
        Assert(await coordinator.ReadLineAsync(queue.Reader, false, CancellationToken.None).WaitAsync(CoordinatorTestWait) == null,
            "A finished queue reads as the end of the stream.");

        ReceiveQueue failed = new();
        failed.TryComplete(new IOException("reset by peer"));
        try
        {
            await coordinator.ReadLineAsync(failed.Reader, false, CancellationToken.None).WaitAsync(CoordinatorTestWait);
            Assert(false, "A queue that failed should throw the reader's error.");
        }
        catch (IOException ex) when (ex.Message == "reset by peer")
        {
        }
    }

    [Test("CoordinatorTimers.CancelledOrReplacedTimerNeverFires")]
    private static void CoordinatorTimersCancelledOrReplacedTimerNeverFires() => RunCancelledOrReplacedTimerNeverFiresAsync().GetAwaiter().GetResult();

    private static async Task RunCancelledOrReplacedTimerNeverFiresAsync()
    {
        List<TaskCompletionSource> waits = new();
        List<Func<CancellationToken, Task>> posted = new();
        SemaphoreSlim postedSignal = new(0);
        List<string> fired = new();

        // The test thread plays the drain loop: it runs the posted work itself.
        CoordinatorTimers<ulong> timers = new(
            work =>
            {
                lock (posted)
                {
                    posted.Add(work);
                }

                postedSignal.Release();
                return true;
            },
            (after, token) =>
            {
                TaskCompletionSource wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Add(wait);
                return wait.Task.WaitAsync(token);
            });

        Func<CancellationToken, Task> Fire(string name) => _ =>
        {
            fired.Add(name);
            return Task.CompletedTask;
        };

        // Replaced: the first timer for a key is cancelled by the second.
        timers.Schedule(1uL, TimeSpan.FromSeconds(3), Fire("replaced"), CancellationToken.None);
        timers.Schedule(1uL, TimeSpan.FromSeconds(3), Fire("replacement"), CancellationToken.None);
        waits[0].SetResult();
        waits[1].SetResult();
        Assert(await postedSignal.WaitAsync(CoordinatorTestWait), "The replacement timer should post its work.");
        Assert(fired.Count == 0, "Nothing may run on the timer's thread.");
        await posted[0](CancellationToken.None);
        Assert(string.Join(",", fired) == "replacement", "Only the replacement may fire, got " + string.Join(",", fired));

        // Cancelled after it elapsed but before the drain loop ran its work.
        timers.Schedule(2uL, TimeSpan.FromSeconds(3), Fire("cancelled"), CancellationToken.None);
        waits[2].SetResult();
        Assert(await postedSignal.WaitAsync(CoordinatorTestWait), "The second timer should post its work.");
        Assert(timers.Cancel(2uL), "A timer whose work is still queued is still pending, so it can be cancelled.");
        await posted[1](CancellationToken.None);
        Assert(string.Join(",", fired) == "replacement", "A cancelled timer must not fire, got " + string.Join(",", fired));
        Assert(!timers.Cancel(2uL) && !timers.IsPending(1uL), "No timer should be left pending.");
        lock (posted)
        {
            Assert(posted.Count == 2, $"The replaced timer must not post anything, got {posted.Count} posts.");
        }
    }
}
