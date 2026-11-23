using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("ClientListenerServer.ConcurrentMixedOutboundPreservesFifo")]
    private static void ClientListenerServerConcurrentMixedOutboundPreservesFifo() =>
        RunConcurrentMixedOutboundPreservesFifoAsync().GetAwaiter().GetResult();

    private static async Task RunConcurrentMixedOutboundPreservesFifoAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.JoinAndDrainAsync(41, "fifo");

        Task[] writes =
        [
            listener.BroadcastEventAsync(new ServerEvent.ServerError("fifo-broadcast"), CancellationToken.None),
            listener.BroadcastEventInterestScopedAsync(new ServerEvent.ServerError("fifo-interest"), CancellationToken.None),
            listener.SendEventToPlayerAsync(41, new ServerEvent.ServerError("fifo-targeted"), CancellationToken.None)
        ];
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(3));

        string[] messages = new string[3];
        for (int i = 0; i < messages.Length; i++)
        {
            ServerEvent evt = await peer.ReadEventAsync();
            Assert(evt is ServerEvent.ServerError, $"FIFO event {i} should be ServerError, got {evt.GetType().Name}.");
            messages[i] = ((ServerEvent.ServerError)evt).Message;
        }
        Assert(messages.SequenceEqual(new[] { "fifo-broadcast", "fifo-interest", "fifo-targeted" }),
            "Mixed outbound operations must retain enqueue order. Got: " + string.Join(",", messages));
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.RejectsSecondJoinOnSameSocket")]
    private static void ClientListenerServerRejectsSecondJoinOnSameSocket() =>
        RunRejectsSecondJoinOnSameSocketAsync().GetAwaiter().GetResult();

    private static async Task RunRejectsSecondJoinOnSameSocketAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await peer.JoinAndDrainAsync(42, "first");

        Guid duplicateId = Guid.NewGuid();
        await peer.SendCommandAsync(new ClientCommand.JoinSession(duplicateId, 43, "second"));
        ServerEvent response = await peer.ReadEventAsync();
        Assert(response is ServerEvent.CommandRejected rejected
            && rejected.CommandId == duplicateId
            && rejected.Reason.Contains("already joined", StringComparison.OrdinalIgnoreCase),
            "A second JoinSession on one socket must be rejected.");
        Assert(listener.GetConnectedPlayerIds().SequenceEqual(new[] { 42 }),
            "Rejected second join must not change the socket's player association.");
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.ReplacementConnectionRemainsSoleTarget")]
    private static void ClientListenerServerReplacementConnectionRemainsSoleTarget() =>
        RunReplacementConnectionRemainsSoleTargetAsync().GetAwaiter().GetResult();

    private static async Task RunReplacementConnectionRemainsSoleTargetAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer oldPeer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await oldPeer.JoinAndDrainAsync(44, "old");
        await using OutboundTestPeer replacement = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await replacement.JoinAndDrainAsync(44, "replacement");

        await WaitForAsync(() => listener.ConnectionCount == 1, TimeSpan.FromSeconds(2));
        Assert(listener.GetConnectedPlayerIds().SequenceEqual(new[] { 44 }),
            "Replacement must be the only active connection for its player.");
        await oldPeer.DisposeAsync();
        await Task.Delay(50);
        Assert(listener.GetConnectedPlayerIds().SequenceEqual(new[] { 44 }),
            "Superseded socket cleanup must preserve the replacement lease.");

        await listener.SendEventToPlayerAsync(
            44,
            new ServerEvent.ServerError("replacement-only"),
            CancellationToken.None);
        ServerEvent targeted = await replacement.ReadEventAsync();
        Assert(targeted is ServerEvent.ServerError { Message: "replacement-only" },
            "Targeted delivery must reach the replacement connection.");
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.EofAndDisposeCompleteWithinBound")]
    private static void ClientListenerServerEofAndDisposeCompleteWithinBound() =>
        RunEofAndDisposeCompleteWithinBoundAsync().GetAwaiter().GetResult();

    private static async Task RunEofAndDisposeCompleteWithinBoundAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        ClientListenerServer listener = new(session, "127.0.0.1:0");
        try
        {
            await listener.StartAsync(CancellationToken.None);
            OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
            await peer.JoinAndDrainAsync(45, "eof");
            await peer.DisposeAsync();
            await WaitForAsync(() => listener.ConnectionCount == 0, TimeSpan.FromSeconds(2));
            await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await session.StopAsync(CancellationToken.None);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Test("OutboundDeliveryPump.FullQueueRejectsSynchronously")]
    private static void OutboundDeliveryPumpFullQueueRejectsSynchronously() =>
        RunFullQueueRejectsSynchronouslyAsync().GetAwaiter().GetResult();
    private static async Task RunFullQueueRejectsSynchronouslyAsync()
    {
        ConcurrentQueue<OutboundFailureKind> failures = new();
        TaskCompletionSource inFlightStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<OutboundDeliveryReceipt> releaseInFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using OutboundDeliveryPump pump = new(
            1,
            (receipt, _, _) =>
            {
                if (!receipt.AllDelivered)
                {
                    failures.Enqueue(receipt.FailureKind);
                }
            });
        try
        {
            OutboundEnqueueReceipt inFlight = pump.TryEnqueue(
                "blocked",
                1,
                requireRecipient: false,
                _ =>
                {
                    inFlightStarted.TrySetResult();
                    return releaseInFlight.Task;
                },
                CancellationToken.None);
            Assert(inFlight.Accepted, "The first operation should enter the pump.");
            await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            OutboundEnqueueReceipt queued = pump.TryEnqueue(
                "queued",
                1,
                requireRecipient: false,
                _ => Task.FromResult(new OutboundDeliveryReceipt(1, 1, 0, OutboundFailureKind.None)),
                CancellationToken.None);
            Assert(queued.Accepted, "The single waiting queue slot should accept one operation.");

            Stopwatch stopwatch = Stopwatch.StartNew();
            OutboundEnqueueReceipt overflow = pump.TryEnqueue(
                "overflow",
                1,
                requireRecipient: false,
                _ => Task.FromResult(new OutboundDeliveryReceipt(1, 1, 0, OutboundFailureKind.None)),
                CancellationToken.None);
            stopwatch.Stop();
            Assert(!overflow.Accepted, "A full outbound queue must reject the overflow operation.");
            Assert(overflow.Completion.IsCompleted, "Queue-full rejection should complete synchronously.");
            Assert(overflow.Completion.Result.FailureKind == OutboundFailureKind.QueueFull,
                "The overflow receipt should identify the full queue.");
            Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
                $"Queue-full rejection must be nonblocking, took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            Assert(failures.Contains(OutboundFailureKind.QueueFull),
                "Queue-full rejection should report an explicit QueueFull receipt.");
        }
        finally
        {
            releaseInFlight.TrySetResult(new OutboundDeliveryReceipt(1, 1, 0, OutboundFailureKind.None));
            await pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test("ClientListenerServer.UnjoinedAdminReceivesGameSpeedBroadcastOnce")]
    private static void ClientListenerServerUnjoinedAdminReceivesGameSpeedBroadcastOnce() =>
        RunUnjoinedAdminReceivesGameSpeedBroadcastOnceAsync().GetAwaiter().GetResult();

    private static async Task RunUnjoinedAdminReceivesGameSpeedBroadcastOnceAsync()
    {
        // simctl/ut.py-style admin connection: deliberately never joins, yet must observe the
        // broadcast-class outcome of its own command (the sender write) exactly once.
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer peer = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);

        Guid commandId = Guid.NewGuid();
        await peer.SendCommandAsync(new ClientCommand.SetGameSpeed(commandId, 0, GameSpeed.Speed3));

        ServerEvent accepted = await peer.ReadEventAsync();
        Assert(accepted is ServerEvent.CommandAccepted commandAccepted && commandAccepted.CommandId == commandId,
            $"set_game_speed should first return CommandAccepted. Got: {accepted}");
        ServerEvent speed = await peer.ReadEventAsync();
        Assert(speed is ServerEvent.GameSpeedChanged changed && changed.Speed == GameSpeed.Speed3,
            $"The unjoined admin sender must receive game_speed_changed. Got: {speed}");
        Assert(await peer.TryReadLineAsync(TimeSpan.FromMilliseconds(500)) == null,
            "The unjoined admin sender must receive game_speed_changed exactly once.");
        await session.StopAsync(CancellationToken.None);
    }

    [Test("ClientListenerServer.JoinedSenderAndPeerReceiveGameSpeedOnce")]
    private static void ClientListenerServerJoinedSenderAndPeerReceiveGameSpeedOnce() =>
        RunJoinedSenderAndPeerReceiveGameSpeedOnceAsync().GetAwaiter().GetResult();

    private static async Task RunJoinedSenderAndPeerReceiveGameSpeedOnceAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer sender = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await sender.JoinAndDrainAsync(61, "sender");
        await using OutboundTestPeer other = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await other.JoinAndDrainAsync(62, "other");

        // The second join fans its SessionJoined out to the first peer — consume it so the
        // speed assertions below see a quiet stream.
        ServerEvent joinFanOut = await sender.ReadEventAsync();
        Assert(joinFanOut is ServerEvent.SessionJoined fanOut && fanOut.PlayerId == 62,
            $"The first peer should see the second peer's SessionJoined broadcast. Got: {joinFanOut}");

        Guid commandId = Guid.NewGuid();
        await sender.SendCommandAsync(new ClientCommand.SetGameSpeed(commandId, 61, GameSpeed.Speed3));

        ServerEvent accepted = await sender.ReadEventAsync();
        Assert(accepted is ServerEvent.CommandAccepted senderAccepted && senderAccepted.CommandId == commandId,
            $"set_game_speed should first return CommandAccepted. Got: {accepted}");
        ServerEvent senderSpeed = await sender.ReadEventAsync();
        Assert(senderSpeed is ServerEvent.GameSpeedChanged senderChanged && senderChanged.Speed == GameSpeed.Speed3,
            $"The joined sender must receive game_speed_changed via its own sender write. Got: {senderSpeed}");

        ServerEvent otherSpeed = await other.ReadEventAsync();
        Assert(otherSpeed is ServerEvent.GameSpeedChanged otherChanged && otherChanged.Speed == GameSpeed.Speed3,
            $"The other joined peer must receive the broadcast game_speed_changed. Got: {otherSpeed}");

        Assert(await sender.TryReadLineAsync(TimeSpan.FromMilliseconds(500)) == null,
            "The joined sender must receive game_speed_changed exactly once (exclude prevents a fan-out copy).");
        Assert(await other.TryReadLineAsync(TimeSpan.FromMilliseconds(500)) == null,
            "The other joined peer must receive game_speed_changed exactly once.");
        await session.StopAsync(CancellationToken.None);
    }

    // a Sim's dialog request goes to the player who claims it, and nobody else.
    [Test("ClientListenerServer.SendsDialogToOwnerOnly")]
    private static void ClientListenerServerSendsDialogToOwnerOnly() =>
        RunSendsDialogToOwnerOnlyAsync().GetAwaiter().GetResult();

    private static async Task RunSendsDialogToOwnerOnlyAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer owner = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await owner.JoinAndDrainAsync(71, "owner");
        await using OutboundTestPeer other = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await other.JoinAndDrainAsync(72, "other");
        ServerEvent fanOut = await owner.ReadEventAsync();
        Assert(fanOut is ServerEvent.SessionJoined, $"The owner sees the other player's join first. Got: {fanOut}");
        await session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 71, 55uL), CancellationToken.None);

        Assert(listener.TryQueueAuthoritativeEvent(new ServerEvent.DialogRequested(3, 55uL, "AcceptCancel", new[] { "Sure?" }), CancellationToken.None),
            "A claimed Sim's dialog is queued for its owner.");
        Assert(!listener.TryQueueAuthoritativeEvent(new ServerEvent.DialogRequested(4, 99uL, "AcceptCancel", new[] { "Sure?" }), CancellationToken.None),
            "An unclaimed Sim's dialog goes nowhere.");
        ServerEvent received = await owner.ReadEventAsync();
        Assert(received is ServerEvent.DialogRequested dialog && dialog.RequestId == 3, $"The owner receives the dialog. Got: {received}");
        Assert(await other.TryReadLineAsync(TimeSpan.FromMilliseconds(500)) == null, "The other player receives nothing.");

        // Seam-gaps row 4: a sting rides the same route (the mod's AudioRecorder -> PumpStings), kind Sting, never answered.
        ServerEvent? sting = BridgeLineCodec.TryDecodeEvent("EVENT|DialogRequest|1000000000|55|" + BridgeLineCodec.Text("Sting") + "|2|"
            + BridgeLineCodec.Text("sting_career_positive") + "|" + BridgeLineCodec.Text("55"));
        Assert(sting is ServerEvent.DialogRequested { Kind: "Sting", SimObjectId: 55uL } decodedSting && decodedSting.Args[0] == "sting_career_positive",
            $"EVENT|DialogRequest of kind Sting decodes with its sound name. Got: {sting}");
        Assert(listener.TryQueueAuthoritativeEvent(sting!, CancellationToken.None), "A claimed Sim's sting is queued for its owner.");
        received = await owner.ReadEventAsync();
        Assert(received is ServerEvent.DialogRequested { Kind: "Sting" }, $"The owner receives the sting. Got: {received}");
        Assert(await other.TryReadLineAsync(TimeSpan.FromMilliseconds(500)) == null, "The other player hears no sting.");
        await session.StopAsync(CancellationToken.None);
    }

    // a pick the server's queue accepted and then dropped (EVENT|PickFailed) reaches the player who made it as
    // the rejection of that command, and nobody else; a pick the session never accepted goes nowhere.
    [Test("ClientListenerServer.SendsAFailedPickToItsPlayerOnly")]
    private static void ClientListenerServerSendsAFailedPickToItsPlayerOnly() =>
        RunSendsAFailedPickToItsPlayerOnlyAsync().GetAwaiter().GetResult();

    private static async Task RunSendsAFailedPickToItsPlayerOnlyAsync()
    {
        Guid commandId = Guid.NewGuid();
        ServerEvent? decoded = BridgeLineCodec.TryDecodeEvent(
            "EVENT|PickFailed|" + commandId.ToString("N") + "|55|9|" + BridgeLineCodec.Text("ObjectInUse"));
        Assert(decoded is ServerEvent.CommandRejected { Reason: "ObjectInUse" } decodedRejection && decodedRejection.CommandId == commandId,
            $"EVENT|PickFailed decodes to the command's rejection. Got: {decoded}");

        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer picker = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await picker.JoinAndDrainAsync(73, "picker");
        await using OutboundTestPeer other = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await other.JoinAndDrainAsync(74, "other");
        ServerEvent fanOut = await picker.ReadEventAsync();
        Assert(fanOut is ServerEvent.SessionJoined, $"The picker sees the other player's join first. Got: {fanOut}");

        InteractionRequest pick = new ServerEvent.UserIntentCaptured(55uL, 9uL, "Sims3.Gameplay.InteractionsShared.Sit+Definition", "Sit", "", "-", null, "").ToRequest();
        CommandResult queued = await session.HandleCommandAsync(new ClientCommand.QueueInteraction(commandId, 73, pick), CancellationToken.None);
        Assert(queued.IsAccepted, "The pick is accepted.");

        Assert(listener.TryQueueAuthoritativeEvent(decoded!, CancellationToken.None), "The failed pick is queued for its player.");
        Assert(!listener.TryQueueAuthoritativeEvent(decoded!, CancellationToken.None), "Once: the second report goes nowhere.");
        Assert(!listener.TryQueueAuthoritativeEvent(new ServerEvent.CommandRejected(Guid.NewGuid(), "ObjectInUse"), CancellationToken.None),
            "A pick the session never accepted goes nowhere.");
        ServerEvent received = await picker.ReadEventAsync();
        Assert(received is ServerEvent.CommandRejected rejected && rejected.CommandId == commandId && rejected.Reason == "ObjectInUse",
            $"The picker receives the rejection. Got: {received}");
        Assert(await other.TryReadLineAsync(TimeSpan.FromMilliseconds(500)) == null, "The other player receives nothing.");
        await session.StopAsync(CancellationToken.None);
    }

    // The server TS3's own speed change (its buttons, or its fast-forward while everyone sleeps) is the authoritative
    // speed: adopted by the session (late joiners get it) and broadcast to every client as GameSpeedChanged.
    [Test("ClientListenerServer.BroadcastsServerSpeedChange")]
    private static void ClientListenerServerBroadcastsServerSpeedChange() =>
        RunBroadcastsServerSpeedChangeAsync().GetAwaiter().GetResult();

    private static async Task RunBroadcastsServerSpeedChangeAsync()
    {
        InMemoryAuthoritativeHost host = new();
        AuthoritativeSession session = new(host);
        await session.StartAsync("Outbound.sims3", CancellationToken.None);
        await using ClientListenerServer listener = new(session, "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        await using OutboundTestPeer first = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await first.JoinAndDrainAsync(81, "first");
        await using OutboundTestPeer second = await OutboundTestPeer.ConnectAsync(listener.BoundEndpoint.Port);
        await second.JoinAndDrainAsync(82, "second");
        Assert(await first.ReadEventAsync() is ServerEvent.SessionJoined, "The first player sees the second join.");

        Assert(listener.TryQueueAuthoritativeEvent(new ServerEvent.SpeedIntentCaptured(GameSpeed.Ultra), CancellationToken.None),
            "The server's speed change is queued.");
        Assert(session.CurrentSpeed == GameSpeed.Ultra, "The session adopts the server's speed.");
        foreach (OutboundTestPeer peer in new[] { first, second })
        {
            ServerEvent received = await peer.ReadEventAsync();
            Assert(received is ServerEvent.GameSpeedChanged changed && changed.Speed == GameSpeed.Ultra, $"Every client gets GameSpeedChanged. Got: {received}");
        }

        await session.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert(stopwatch.Elapsed < timeout, $"Condition was not met within {timeout}.");
            await Task.Delay(10);
        }
    }

    private sealed class OutboundTestPeer : IAsyncDisposable
    {
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;

        private OutboundTestPeer(TcpClient client)
        {
            Client = client;
            _stream = client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        }

        public TcpClient Client { get; }
        public int Available => Client.Available;

        public static async Task<OutboundTestPeer> ConnectAsync(int port, int? receiveBufferSize = null)
        {
            TcpClient client = new();
            if (receiveBufferSize.HasValue)
            {
                client.ReceiveBufferSize = receiveBufferSize.Value;
            }
            await client.ConnectAsync(IPAddress.Loopback, port);
            return new OutboundTestPeer(client);
        }

        public async Task JoinAndDrainAsync(int playerId, string displayName)
        {
            Guid commandId = Guid.NewGuid();
            await SendCommandAsync(new ClientCommand.JoinSession(commandId, playerId, displayName));
            ServerEvent first = await ReadEventAsync();
            ServerEvent second = await ReadEventAsync();
            ServerEvent third = await ReadEventAsync();
            Assert(first is ServerEvent.CommandAccepted accepted && accepted.CommandId == commandId,
                "Join should first return its CommandAccepted event.");
            Assert(second is ServerEvent.SessionJoined, "Join should then emit SessionJoined.");
            Assert(third is ServerEvent.FullSnapshot, "Join should then emit the FullSnapshot baseline.");
        }

        public async Task SendCommandAsync(ClientCommand command)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(PublicProtocolCodec.EncodeCommand(command) + "\n");
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }

        public async Task<ServerEvent> ReadEventAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            string? line = await _reader.ReadLineAsync(timeout.Token);
            Assert(line != null, "Expected a server event before EOF.");
            return PublicProtocolCodec.DecodeEvent(line!);
        }

        /// <summary>
        /// Read one line within a timeout; null means silence (no event arrived in the
        /// window). Cancels the pending read, so it must be the peer's LAST read.
        /// </summary>
        public async Task<string?> TryReadLineAsync(TimeSpan timeout)
        {
            using CancellationTokenSource timeoutSource = new(timeout);
            try
            {
                return await _reader.ReadLineAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _stream.Dispose();
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
