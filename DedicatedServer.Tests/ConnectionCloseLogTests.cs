using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// G14: every client-connection close writes one line: when, which player, the peer, its age, bytes, last
/// operation and why. The lease-expired close (a join whose lease another join replaced before it activated) is a race
/// this harness cannot drive.
/// </summary>
internal static partial class Program
{
    [Test("ClientListenerServer.CloseLogNamesAPeerThatLeft")]
    private static void ClientListenerServerCloseLogNamesAPeerThatLeft() =>
        RunCloseLogNamesAPeerThatLeftAsync().GetAwaiter().GetResult();

    private static async Task RunCloseLogNamesAPeerThatLeftAsync()
    {
        ConcurrentQueue<string> closes = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: closes.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        string remote = "127.0.0.1:" + ((IPEndPoint)peer.Client.Client.LocalEndPoint!).Port;
        await peer.JoinAndDrainAsync(51, "leaver");
        await peer.DisposeAsync();

        string line = await WaitForCloseLineAsync(closes, "player=51");
        Assert(Regex.IsMatch(line, @"^\[client-conn\] closed utc=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z player=51 remote="
                + Regex.Escape(remote) + @" age=\d+\.\ds in=[1-9]\d*B out=[1-9]\d*B last=send:\w+ reason=normal$"),
            "A peer that closed its end should log a normal close with its endpoint, age, bytes and last op: " + line);
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.CloseLogNamesTheSupersedingConnection")]
    private static void ClientListenerServerCloseLogNamesTheSupersedingConnection() =>
        RunCloseLogNamesTheSupersedingConnectionAsync().GetAwaiter().GetResult();

    private static async Task RunCloseLogNamesTheSupersedingConnectionAsync()
    {
        ConcurrentQueue<string> closes = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: closes.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer oldPeer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        string oldRemote = "127.0.0.1:" + ((IPEndPoint)oldPeer.Client.Client.LocalEndPoint!).Port;
        await oldPeer.JoinAndDrainAsync(52, "old");
        await using OutboundTestPeer replacement = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        int replacementPort = ((IPEndPoint)replacement.Client.Client.LocalEndPoint!).Port;
        await replacement.JoinAndDrainAsync(52, "replacement");

        string line = await WaitForCloseLineAsync(closes, "player=52");
        Assert(line.Contains($" remote={oldRemote} ", StringComparison.Ordinal)
                && line.EndsWith($" by=127.0.0.1:{replacementPort} reason=superseded-by {replacementPort}", StringComparison.Ordinal),
            "A superseded connection should log the superseding connection's endpoint and port: " + line);
        Assert(closes.Count(entry => entry.StartsWith("[client-conn] closed ", StringComparison.Ordinal)) == 1,
            "The replacement connection is still open: " + string.Join(" | ", closes));
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.CloseLogNamesAnOversizedLine")]
    private static void ClientListenerServerCloseLogNamesAnOversizedLine() =>
        RunCloseLogNamesAnOversizedLineAsync().GetAwaiter().GetResult();

    private static async Task RunCloseLogNamesAnOversizedLineAsync()
    {
        ConcurrentQueue<string> closes = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        await using ClientListenerServer listener = new(
            session, "127.0.0.1:0", maxCommandLineChars: 256, connectionLog: closes.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.Client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(new string('x', 4096)));

        string line = await WaitForCloseLineAsync(closes, "reason=");
        Assert(line.Contains(" player=none ", StringComparison.Ordinal) && line.EndsWith(" reason=line-too-long", StringComparison.Ordinal),
            "An oversized line should log line-too-long for an unjoined connection: " + line);
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.CloseLogNamesAReadError")]
    private static void ClientListenerServerCloseLogNamesAReadError() =>
        RunCloseLogNamesAReadErrorAsync().GetAwaiter().GetResult();

    private static async Task RunCloseLogNamesAReadErrorAsync()
    {
        ConcurrentQueue<string> closes = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: closes.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.JoinAndDrainAsync(53, "reset");
        peer.Client.LingerState = new LingerOption(true, 0);
        peer.Client.Client.Close(); // a zero linger closes with a reset, not a FIN
        await peer.DisposeAsync();

        string line = await WaitForCloseLineAsync(closes, "player=53");
        Assert(Regex.IsMatch(line, @" reason=read-error Connection(Reset|Aborted)$"),
            "A peer that reset the connection should log the read error's socket code: " + line);
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.CloseLogClassifiesWriteFailures")]
    private static void ClientListenerServerCloseLogClassifiesWriteFailures()
    {
        // A write over the broadcast timeout cannot be driven end to end reliably because loopback buffering varies by platform.
        Assert(ClientListenerServer.WriteFailureReason(new OperationCanceledException(), timedOut: true, shuttingDown: false)
            == "broadcast-timeout", "A write cancelled by its own timeout is a broadcast timeout.");
        Assert(ClientListenerServer.WriteFailureReason(
                new IOException("aborted", new SocketException((int)SocketError.ConnectionAborted)), timedOut: false, shuttingDown: false)
            == "write-error ConnectionAborted", "A socket error names its code.");
        Assert(ClientListenerServer.WriteFailureReason(new IOException("x"), timedOut: true, shuttingDown: true) == "shutdown",
            "A write the server's own stop cancelled is a shutdown.");
        Assert(ClientListenerServer.WriteFailureReason(new InvalidOperationException("x"), timedOut: false, shuttingDown: false)
            == "write-error InvalidOperationException", "Any other failure names its type.");
    }

    [Test("ClientListenerServer.EncodeFailureNamesTheNonFiniteField")]
    private static void ClientListenerServerEncodeFailureNamesTheNonFiniteField() =>
        RunEncodeFailureNamesTheNonFiniteFieldAsync().GetAwaiter().GetResult();

    private static async Task RunEncodeFailureNamesTheNonFiniteFieldAsync()
    {
        ConcurrentQueue<string> lines = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: lines.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.JoinAndDrainAsync(56, "nan");

        bool threw = false;
        try
        {
            await listener.SendEventToPlayerAsync(56, new ServerEvent.SimMotiveChanged(9uL, "Hunger", float.NaN), CancellationToken.None);
        }
        catch (ArgumentException)
        {
            threw = true; // as before: the codec's exception still reaches the caller
        }

        Assert(threw, "An event the codec refuses should still throw to its caller.");
        string line = await WaitForCloseLineAsync(lines, "encode failed");
        Assert(Regex.IsMatch(line, @"^\[client-conn\] encode failed utc=\S+ event=SimMotiveChanged to=player:56 non-finite=Value=NaN error=ArgumentException: "),
            "An encode failure should name the event, its target, the non-finite field and the error: " + line);
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.CloseLogNamesAnEncodeThrow")]
    private static void ClientListenerServerCloseLogNamesAnEncodeThrow() =>
        RunCloseLogNamesAnEncodeThrowAsync().GetAwaiter().GetResult();

    private static async Task RunCloseLogNamesAnEncodeThrowAsync()
    {
        // A NaN in the world: the join's FullSnapshot cannot be encoded, the throw ends the read loop, and the
        // connection closes (as before) with the event and the error in its close line.
        ConcurrentQueue<string> lines = new();
        InMemoryAuthoritativeHost host = new();
        SimulationSnapshot world = host.ActiveSnapshot;
        host.SetActiveSnapshot(world with
        {
            Sims = world.Sims.Select((sim, i) => i == 0 ? sim with { Position = new Vector3Dto(float.NaN, 0f, 0f) } : sim).ToList()
        });
        AuthoritativeSession session = new(host);
        await session.StartAsync("CloseLog.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: lines.Enqueue);
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.SendCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 57, "nan"));

        string closed = await WaitForCloseLineAsync(lines, "] closed ");
        Assert(Regex.IsMatch(closed, @" player=57 .* last=encode:FullSnapshot reason=encode-throw FullSnapshot ArgumentException: "),
            "An encode throw on a reply should close with reason=encode-throw and the event type: " + closed);
        string encode = lines.First(line => line.Contains("encode failed", StringComparison.Ordinal));
        Assert(Regex.IsMatch(encode, @" event=FullSnapshot to=player:57@127\.0\.0\.1:\d+ non-finite=Snapshot\.Sims\[0\]\.Position\.X=NaN error="),
            "The encode line should name the non-finite field: " + encode);
        await session.StopAsync(CancellationToken.None);
    }

    private sealed record NonFiniteInner(float X, double Y);

    private sealed record NonFiniteOuter(string Name, List<NonFiniteInner> Items, Dictionary<string, NonFiniteInner> ByName, double Speed);

    [Test("NonFiniteValues.NamesEveryPath")]
    private static void NonFiniteValuesNamesEveryPath()
    {
        NonFiniteOuter outer = new(
            "x",
            new List<NonFiniteInner> { new(1f, 2.0), new(float.PositiveInfinity, double.NaN) },
            new Dictionary<string, NonFiniteInner> { ["pablo"] = new(float.NaN, 0.0) },
            double.NegativeInfinity);
        string found = string.Join(",", NonFiniteValues.Find(outer));
        Assert(found == "Items[1].X=Infinity,Items[1].Y=NaN,ByName[pablo].X=NaN,Speed=-Infinity",
            "Every non-finite value should be named by its path: " + found);
        Assert(NonFiniteValues.Find(new ServerEvent.SimMotiveChanged(9uL, "Hunger", 0.5f)).Count == 0, "A finite event has none.");
    }

    [Test("ClientListenerServer.CloseLogNamesAShutdown")]
    private static void ClientListenerServerCloseLogNamesAShutdown() =>
        RunCloseLogNamesAShutdownAsync().GetAwaiter().GetResult();

    private static async Task RunCloseLogNamesAShutdownAsync()
    {
        ConcurrentQueue<string> closes = new();
        AuthoritativeSession session = await StartCloseLogSessionAsync();
        ClientListenerServer listener = new(session, "127.0.0.1:0", connectionLog: closes.Enqueue);
        try
        {
            await listener.StartAsync(CancellationToken.None);
            await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
            await peer.JoinAndDrainAsync(55, "stays");
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

            string line = await WaitForCloseLineAsync(closes, "player=55");
            Assert(line.EndsWith(" reason=shutdown", StringComparison.Ordinal), "The server's own shutdown should log shutdown: " + line);
        }
        finally
        {
            await listener.DisposeAsync();
            await session.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<AuthoritativeSession> StartCloseLogSessionAsync()
    {
        AuthoritativeSession session = new(new InMemoryAuthoritativeHost());
        await session.StartAsync("CloseLog.sims3", CancellationToken.None);
        return session;
    }

    /// <summary>The first logged entry containing <paramref name="match"/>; join blocks only when the match is one.</summary>
    private static async Task<string> WaitForCloseLineAsync(ConcurrentQueue<string> closes, string match)
    {
        bool joinBlocks = match.StartsWith("[join]", StringComparison.Ordinal);
        string? found = null;
        await WaitForAsync(
            () => (found = closes.FirstOrDefault(line => line.Contains(match, StringComparison.Ordinal)
                && (joinBlocks || !line.StartsWith("[join]", StringComparison.Ordinal)))) != null,
            TimeSpan.FromSeconds(5));
        return found!;
    }
}
