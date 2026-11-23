using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// (bad-join-20261020): the client always drains its socket, whatever its handlers do. A handler that waits on
/// the client TS3 (the authority publish on the join's command_accepted once took 5.6 s) used to leave the socket
/// unread while the server's broadcasts filled the buffers, until a write hit the server's 5 s broadcast timeout and
/// the connection broke.
/// </summary>
internal static partial class Program
{
    [Test("SimsOnlineClient.DrainsItsSocketWhileAHandlerStalls")]
    private static void SimsOnlineClientDrainsItsSocketWhileAHandlerStalls() =>
        RunClientDrainsItsSocketWhileAHandlerStallsAsync().GetAwaiter().GetResult();

    private static async Task RunClientDrainsItsSocketWhileAHandlerStallsAsync()
    {
        const int player = 61;
        const int burst = 1500; // 1500 events of about 4 KB: 6 MB; the stalled client used to take about 180 KB
        ConcurrentQueue<string> closes = new();
        AuthoritativeSession session = new(new InMemoryAuthoritativeHost());
        await session.StartAsync("Drain.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: closes.Enqueue); // the real 5 s broadcast timeout
        await listener.StartAsync(CancellationToken.None);
        string repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        Assert(File.Exists(ClientSubprocessArtifact(repoRoot)), "SimsOnline.Client not found. Build the project first.");
        // The first event's handling (the join's command_accepted, as on the live failure) waits 6 s.
        ProcessStartInfo psi = NewClientProcessStartInfo(repoRoot,
            $"--endpoint 127.0.0.1:{listener.BoundEndpoint.Port} --player {player} --display drain --role client "
            + "--exit-after-role-changed --json-stdout --connect-timeout 15 --stall-first-dispatch-ms 6000");
        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the client.");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await WaitForAsync(() => listener.GetConnectedPlayerIds().Contains(player), TimeSpan.FromSeconds(15));
            string pad = new('x', 4000);
            Stopwatch sent = Stopwatch.StartNew();
            for (int i = 0; i < burst; i++)
            {
                await listener.BroadcastEventAsync(new ServerEvent.ServerError($"burst-{i:D4} {pad}"), CancellationToken.None);
            }

            Console.WriteLine($"  burst of {burst} sent in {sent.Elapsed.TotalSeconds:F1} s");
            using CancellationTokenSource exit = new(TimeSpan.FromSeconds(60));
            await proc.WaitForExitAsync(exit.Token);
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        string[] timeouts = closes.Where(line => line.Contains("broadcast-timeout", StringComparison.Ordinal)).ToArray();
        Assert(timeouts.Length == 0, "No server write may time out while the client's handler stalls: " + string.Join(" | ", timeouts));
        Assert(proc.ExitCode == 0, $"The client should reach --exit-after-role-changed (exit 0), got {proc.ExitCode}. STDERR: {stderr}");

        List<int> received = new(), dispatched = new();
        bool behind = false;
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(line);
            string kind = doc.RootElement.GetProperty("kind").GetString() ?? string.Empty;
            JsonElement payload = doc.RootElement.GetProperty("payload");
            string text = kind switch
            {
                "received" => payload.GetString() ?? string.Empty,
                "dispatched" => payload.GetProperty("head").GetString() ?? string.Empty,
                _ => string.Empty
            };
            behind |= kind == "receive_queue_behind";
            Match m = Regex.Match(text, @"burst-(\d{4}) ");
            if (m.Success)
            {
                (kind == "received" ? received : dispatched).Add(int.Parse(m.Groups[1].Value));
            }
        }

        Assert(received.SequenceEqual(Enumerable.Range(0, burst)), $"Every burst event should arrive once, in order (got {received.Count}).");
        Assert(dispatched.SequenceEqual(Enumerable.Range(0, burst)), $"Every burst event should be handled once, in order (got {dispatched.Count}).");
        Assert(behind, "The stalled handler should show as a receive_queue_behind line (over 1000 lines queued).");
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ReceiveQueue.KeepsOrderAndRethrowsTheReadersError")]
    private static void ReceiveQueueKeepsOrderAndRethrowsTheReadersError() =>
        RunReceiveQueueKeepsOrderAndRethrowsTheReadersErrorAsync().GetAwaiter().GetResult();

    private static async Task RunReceiveQueueKeepsOrderAndRethrowsTheReadersErrorAsync()
    {
        ReceiveQueue queue = new();
        for (int i = 0; i < 3; i++)
        {
            queue.TryWrite("line-" + i);
        }

        Assert(queue.Depth == 3 && queue.Reader.Count == 3, "The queue counts its lines.");
        Assert(queue.Reader.TryPeek(out string? head) && head == "line-0", "Peek shows the oldest line.");
        queue.TryComplete(new IOException("Server closed the connection."));
        List<string> read = new();
        bool threw = false;
        try
        {
            await foreach (string line in queue.Reader.ReadAllAsync())
            {
                read.Add(line);
            }
        }
        catch (IOException ex) when (ex.Message == "Server closed the connection.")
        {
            threw = true;
        }

        Assert(read.SequenceEqual(new[] { "line-0", "line-1", "line-2" }), "Lines come out in arrival order: " + string.Join(",", read));
        Assert(threw, "Once its lines are read, the queue rethrows the receive task's own error (not a ChannelClosedException).");
    }

    [Test("ReceiveQueue.WarnsWhileItsHandlersAreBehind")]
    private static void ReceiveQueueWarnsWhileItsHandlersAreBehind() =>
        RunReceiveQueueWarnsWhileItsHandlersAreBehindAsync().GetAwaiter().GetResult();

    private static async Task RunReceiveQueueWarnsWhileItsHandlersAreBehindAsync()
    {
        ConcurrentQueue<ReceiveLag> reports = new();
        ReceiveQueue queue = new();
        using CancellationTokenSource stop = new();
        Task watch = queue.WatchAsync(3, TimeSpan.FromMilliseconds(300), reports.Enqueue, stop.Token,
            every: TimeSpan.FromMilliseconds(20), repeat: TimeSpan.FromSeconds(30));
        for (int i = 0; i < 5; i++)
        {
            queue.TryWrite("line-" + i);
        }

        await WaitForAsync(() => !reports.IsEmpty, TimeSpan.FromSeconds(3));
        Assert(reports.TryPeek(out ReceiveLag first) && first.Behind && first.Depth == 5, $"Five lines over a limit of three are behind: {first}");
        while (queue.Reader.TryRead(out _))
        {
        }

        await WaitForAsync(() => reports.Any(lag => !lag.Behind), TimeSpan.FromSeconds(3));
        ReceiveLag caughtUp = reports.First(lag => !lag.Behind);
        Assert(caughtUp.Depth == 0 && caughtUp.MaxDepth == 5, $"The caught-up line carries the worst depth seen: {caughtUp}");
        Assert(reports.Count(lag => lag.Behind) == 1, "One behind line per episode within the repeat interval.");

        queue.TryWrite("old"); // one line, under the depth limit: behind once it has waited 300 ms
        await WaitForAsync(() => reports.Count(lag => lag.Behind) == 2, TimeSpan.FromSeconds(3));
        ReceiveLag aged = reports.Last(lag => lag.Behind);
        Assert(aged.Depth == 1 && aged.OldestSeconds >= 0.3, $"An old line alone is behind by its age: {aged}");
        stop.Cancel();
        await watch.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
