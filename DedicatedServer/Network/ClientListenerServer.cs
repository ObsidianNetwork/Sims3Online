using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Observability;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Network;

/// <summary>
/// P3 (M3 Two-Process Coordination) — public TCP listener for external clients.
///
/// Accepts newline-delimited JSON <see cref="ClientCommand"/> frames on the configured
/// endpoint, dispatches them through the authoritative session, and writes the resulting
/// <see cref="CommandResult.Events"/> back as newline-delimited JSON. Asynchronous
/// <see cref="ServerEvent"/> pushes (interaction lifecycle, ownership changes, etc.) are
/// broadcast to every connected client.
///
/// This is the bootstrap: this slice only handles JoinSession / LeaveSession /
/// RequestSnapshot end-to-end. ClaimSim / QueueInteraction ownership rules land in a
/// later P3.x slice.
/// </summary>
public sealed class ClientListenerServer : IAsyncDisposable
{
    private const int DefaultOutboundQueueCapacity = 256;

    /// <summary>
    /// Longest command line a client may send, in characters. Real commands are a few hundred characters; the
    /// largest (<see cref="ClientCommand.SubmitLotExport"/>) is expected to stay well below this. A longer line closes the connection.
    /// </summary>
    public const int DefaultMaxCommandLineChars = 4 * 1024 * 1024;
    private static readonly TimeSpan BroadcastWriteTimeout = TimeSpan.FromSeconds(5);
    private readonly AuthoritativeSession _session;
    private readonly IPEndPoint _endpoint;
    private readonly TcpListener _listener;
    private readonly Action<Exception, string>? _failureReporter;
    private readonly Func<ClientCommand, SimsOnlineTelemetryTransaction>? _transactionFactory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<ClientConnection> _connections = new();
    private readonly object _connectionsLock = new();
    private readonly OutboundDeliveryPump _outbound;
    private readonly int _maxCommandLineChars;
    private readonly TimeSpan _broadcastWriteTimeout;
    private readonly Action<string> _connectionLog;
    // Wave 7 · card 35 gate 11: one sidecar connection per player the controller
    // has authenticated, outliving the ClientConnection that first spoke for them.
    // The kernel's claim authority is scoped to the sidecar TCP connection (its
    // daemon PlayerId is assigned in connect order); re-attaching per client
    // connection would make a reconnecting player reclaim as a NEW daemon player
    // against claims the OLD one still holds — an ownership conflict the seam
    // would invent. Entries are only retired when the listener itself stops:
    // synthesizing a LeaveSession on disconnect must not burn the player's
    // daemon identity, because the reclaim rides it.
    private readonly Dictionary<int, PlayerSidecar> _playerSidecars = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public ClientListenerServer(
        AuthoritativeSession session,
        string endpoint,
        Action<Exception, string>? failureReporter = null,
        Func<ClientCommand, SimsOnlineTelemetryTransaction>? transactionFactory = null,
        int outboundQueueCapacity = DefaultOutboundQueueCapacity,
        int maxCommandLineChars = DefaultMaxCommandLineChars,
        TimeSpan? broadcastWriteTimeout = null,
        Action<string>? connectionLog = null)
    {
        _session = session;
        _endpoint = ParseEndpoint(endpoint);
        _listener = new TcpListener(_endpoint);
        _failureReporter = failureReporter;
        _transactionFactory = transactionFactory;
        _outbound = new OutboundDeliveryPump(outboundQueueCapacity, ReportOutboundReceipt);
        _maxCommandLineChars = maxCommandLineChars;
        _broadcastWriteTimeout = broadcastWriteTimeout ?? BroadcastWriteTimeout;
        _connectionLog = connectionLog ?? Console.WriteLine;
    }

    /// <summary>
    /// One player's sidecar attachment. <see cref="Gate"/> serializes use of the
    /// underlying line socket (fenced batches are per-connection state); a null
    /// <see cref="Sidecar"/> means the seam is off for this player (transport
    /// failure, or the daemon refused the attestation) — never a native failure.
    /// </summary>
    internal sealed class PlayerSidecar
    {
        public readonly object Gate = new();
        public RustAuthoritySidecar? Sidecar;
        public string? HelloRejection;
    }

    /// <summary>Acquire (once) the sidecar attachment the given player id speaks through.</summary>
    internal PlayerSidecar AcquirePlayerSidecar(int playerId, string remoteText)
    {
        lock (_playerSidecars)
        {
            if (_playerSidecars.TryGetValue(playerId, out PlayerSidecar? existing) && existing is not null)
            {
                return existing;
            }
            return _playerSidecars[playerId] = AttachPlayerSidecar(playerId, remoteText);
        }
    }

    /// <summary>
    /// Drop a dead attachment so the next acquire retries once (a restarted daemon is
    /// reachable again; claims made before the death are gone with its world, and the
    /// retry attaches a fresh daemon player the client re-claims onto).
    /// </summary>
    internal void NoteSidecarTransportDead(int playerId)
    {
        lock (_playerSidecars)
        {
            if (_playerSidecars.Remove(playerId, out PlayerSidecar? entry) && entry is not null)
            {
                try { entry.Sidecar?.Dispose(); } catch { /* gone */ }
            }
        }
    }

    private PlayerSidecar AttachPlayerSidecar(int playerId, string remoteText)
    {
        var entry = new PlayerSidecar();
        entry.Sidecar = RustAuthoritySidecar.TryOpen(
            RustAuthoritySidecar.ClientEnv.FromControllerEnvironment(), RustAuthoritySidecar.DefaultIoTimeout,
            out entry.HelloRejection, out string? transportFailure);
        if (transportFailure is not null)
        {
            LogConnection($"[sidecar] unavailable player={playerId} remote={remoteText} error={transportFailure}; seam inert");
        }
        else if (entry.HelloRejection is not null)
        {
            LogConnection($"[sidecar] hello gate refused player={playerId} remote={remoteText} reason={entry.HelloRejection}");
        }
        return entry;
    }

    /// <summary>
    /// G14: the one line each client connection writes when it closes (server.log by default): when, which
    /// player, the peer, how long it lived, its bytes, its last operation and why it closed.
    /// </summary>
    internal void LogConnection(string line) => _connectionLog(line);

    /// <summary>
    /// G14: encode an event. When the codec throws (System.Text.Json refuses NaN and Infinity), write one line
    /// naming the event, where it was going, its non-finite fields and the error, then rethrow as before.
    /// </summary>
    private string EncodeEvent(ServerEvent serverEvent, string target, ClientConnection? connection = null)
    {
        try
        {
            string line = PublicProtocolCodec.EncodeEvent(serverEvent);
            JoinTrace.Current?.Encoded(serverEvent.GetType().Name, line, broadcast: target == "broadcast");
            return line;
        }
        catch (Exception ex)
        {
            string name = serverEvent.GetType().Name;
            connection?.NoteEncodeFailure(name, ex);
            string to = connection == null
                ? target
                : (connection.AssociatedPlayerId is int player ? $"player:{player}@" : string.Empty) + connection.RemoteEndpointText;
            IReadOnlyList<string> nonFinite = NonFiniteValues.Find(serverEvent);
            _connectionLog($"[client-conn] encode failed utc={ClientConnection.UtcStamp()} event={name} to={to} "
                + $"non-finite={(nonFinite.Count == 0 ? "none" : string.Join(",", nonFinite))} error={ClientConnection.Describe(ex)}");
            throw;
        }
    }

    /// <summary>Why a write to a connection failed: the server stopping, the write timeout, else the socket error.</summary>
    internal static string WriteFailureReason(Exception ex, bool timedOut, bool shuttingDown) =>
        shuttingDown ? "shutdown" : timedOut ? "broadcast-timeout" : "write-error " + ClientConnection.ErrorCode(ex);

    /// <summary>
    /// A warning to print when the listener accepts connections from other machines, or null when it is
    /// loopback-only. The public protocol has no authentication (SECURITY.md).
    /// </summary>
    public string? ExposureWarning => IPAddress.IsLoopback(_endpoint.Address)
        ? null
        : $"WARNING: the public client listener accepts connections from other machines ({_endpoint}). The protocol has "
            + "no authentication: anyone who can reach this port can join and command Sims. Use a private network "
            + "(see SECURITY.md) and never forward this port on a router.";

    /// <summary>Address the listener bound to (resolved after Start).</summary>
    public IPEndPoint BoundEndpoint
    {
        get
        {
            if (_listener.Server.LocalEndPoint is IPEndPoint endpoint)
            {
                return endpoint;
            }

            return _endpoint;
        }
    }

    /// <summary>Number of currently-attached client connections.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_connectionsLock)
            {
                return _connections.Count;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => RunAcceptLoopAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Push an authoritative event to every connected client. Survivors are kept;
    /// connections that fault during the write are scheduled for disposal.
    /// </summary>
    public Task BroadcastEventAsync(ServerEvent serverEvent, CancellationToken cancellationToken) =>
        BroadcastEventAsync(serverEvent, exclude: null, cancellationToken);

    /// <summary>
    /// Broadcast variant that skips one connection (typically the command sender, which the
    /// caller writes to separately). Unjoined admin connections are never in the ACTIVE set, so
    /// without the separate sender write their command's broadcast-class outcome event would
    /// never reach them (simctl speed/worldtime hang waiting on it).
    /// </summary>
    internal Task BroadcastEventAsync(ServerEvent serverEvent, ClientConnection? exclude, CancellationToken cancellationToken) =>
        AwaitBestEffortAsync(QueueBroadcast(
            new[] { EncodeEvent(serverEvent, "broadcast") },
            signal: serverEvent.GetType().Name,
            requireRecipient: false,
            cancellationToken: cancellationToken,
            exclude: exclude));

    internal Task<bool> EnqueueEventBatchAsync(
        IReadOnlyList<ServerEvent> serverEvents,
        CancellationToken cancellationToken)
    {
        string[] lines = serverEvents.Select(e => EncodeEvent(e, "broadcast")).ToArray();
        // QueueBroadcast's recipients, taken here so the presentation count names the players it went to.
        OutboundRecipient[] recipients = SnapshotActiveConnections()
            .Select(connection => new OutboundRecipient(connection, lines))
            .ToArray();
        return AwaitAllDeliveredAsync(QueuePresentation(serverEvents, recipients, "motive_batch", cancellationToken));
    }

    /// <summary>the presentation lines queued for each player, by kind (the admin listener's PRESENTATION_SENT).</summary>
    internal PresentationSentCounts PresentationSent { get; } = new();

    // A batch that can carry presentation lines: queued as QueueRecipients does, a recipient required, and once the
    // transport accepts it its SimAnimation lines count for each recipient's player.
    private OutboundEnqueueReceipt QueuePresentation(
        IReadOnlyList<ServerEvent> serverEvents,
        OutboundRecipient[] recipients,
        string signal,
        CancellationToken cancellationToken)
    {
        OutboundEnqueueReceipt receipt = QueueRecipients(recipients, signal, requireRecipient: true, cancellationToken);
        if (receipt.Accepted)
        {
            PresentationSent.Add(serverEvents, recipients.Select(recipient => recipient.Connection.AssociatedPlayerId).OfType<int>());
        }

        return receipt;
    }

    /// <summary>a batch for every joined player except these: the presentation stream while their history is
    /// gathered (held for them, sent after it). False when nobody received it.</summary>
    internal Task<bool> EnqueueEventBatchExceptAsync(
        IReadOnlyList<ServerEvent> serverEvents,
        IReadOnlyCollection<int> exceptPlayers,
        CancellationToken cancellationToken)
    {
        string[] lines = serverEvents.Select(e => EncodeEvent(e, "broadcast")).ToArray();
        OutboundRecipient[] recipients = SnapshotActiveConnections()
            .Where(connection => !(connection.AssociatedPlayerId is int player && exceptPlayers.Contains(player)))
            .Select(connection => new OutboundRecipient(connection, lines))
            .ToArray();
        return AwaitAllDeliveredAsync(QueuePresentation(serverEvents, recipients, "motive_batch", cancellationToken));
    }

    /// <summary>a batch for one joined player (the presentation history a joiner needs). False when
    /// the player has no live connection or the batch was not delivered.</summary>
    internal Task<bool> EnqueueEventBatchToPlayerAsync(
        int playerId,
        IReadOnlyList<ServerEvent> serverEvents,
        CancellationToken cancellationToken)
    {
        ClientConnection? target = FindActivePlayerConnection(playerId);
        if (target == null)
        {
            return Task.FromResult(false);
        }

        string[] lines = serverEvents.Select(e => EncodeEvent(e, "player:" + playerId)).ToArray();
        return AwaitAllDeliveredAsync(QueuePresentation(
            serverEvents,
            new[] { new OutboundRecipient(target, lines) },
            "presentation_history",
            cancellationToken));
    }

    public bool TryQueueAuthoritativeEvent(ServerEvent serverEvent, CancellationToken cancellationToken)
    {
        if (serverEvent is ServerEvent.SpeedIntentCaptured hostSpeed)
        {
            // The server TS3's own speed change (its buttons, or the game's fast-forward while every Sim sleeps): the
            // server is the authority, so every client follows it.
            _session.AdoptHostSpeed(hostSpeed.Speed);
            serverEvent = new ServerEvent.GameSpeedChanged(hostSpeed.Speed);
        }

        if (serverEvent is ServerEvent.DialogRequested dialog)
        {
            // a Sim's dialog goes to the player who claims it, and nobody else.
            if (!_session.Claims.TryGetValue(dialog.SimObjectId, out int owner))
            {
                return false;
            }

            return QueueTargeted(
                FindActivePlayerConnection(owner),
                new[] { EncodeEvent(serverEvent, "player:" + owner) },
                signal: "dialog_request",
                cancellationToken).Accepted;
        }

        if (serverEvent is ServerEvent.CommandRejected pickFailed)
        {
            // a player's pick the server's queue accepted and then dropped (EVENT|PickFailed) goes to the
            // player who made it, as the rejection of that command, and nobody else.
            if (!_session.TryTakePickRequester(pickFailed.CommandId, out int requester))
            {
                return false;
            }

            return QueueTargeted(
                FindActivePlayerConnection(requester),
                new[] { EncodeEvent(serverEvent, "player:" + requester) },
                signal: "pick_failed",
                cancellationToken).Accepted;
        }

        OutboundEnqueueReceipt enqueueReceipt = serverEvent is ServerEvent.InteractionQueued
            or ServerEvent.InteractionReplayQueued
            ? QueueInterestScopedEvent(serverEvent, cancellationToken)
            : QueueBroadcast(
                new[] { EncodeEvent(serverEvent, "broadcast") },
                serverEvent.GetType().Name,
                requireRecipient: false,
                cancellationToken);
        return enqueueReceipt.Accepted;
    }

    /// <summary>Player ids with an associated live TCP connection (post-JoinSession).</summary>
    public IReadOnlyList<int> GetConnectedPlayerIds()
    {
        lock (_connectionsLock)
        {
            return _connections
                .Where(IsActiveConnection)
                .Select(static connection => connection.AssociatedPlayerId!.Value)
                .Distinct()
                .ToArray();
        }
    }

    /// <summary>
    /// push a snapshot delta to one player (interest-scoped). Used by per-player resync.
    /// </summary>
    public Task SendSnapshotDeltaToPlayerAsync(
        int playerId,
        SimulationSnapshot snapshot,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        ClientConnection? target = FindActivePlayerConnection(playerId);
        SimulationSnapshot scoped = _session.ApplyMirrorScopeForPlayer(playerId, snapshot);
        ServerEvent evt = forceFull
            ? new ServerEvent.SnapshotDelta(scoped, ForceFull: true)
            : new ServerEvent.SnapshotDelta(scoped);
        return AwaitBestEffortAsync(QueueTargeted(
            target,
            new[] { EncodeEvent(evt, "player:" + playerId) },
            signal: "snapshot_delta_targeted",
            cancellationToken));
    }

    /// <summary>push a session event to one joined player.</summary>
    public Task SendEventToPlayerAsync(
        int playerId,
        ServerEvent serverEvent,
        CancellationToken cancellationToken)
        => AwaitBestEffortAsync(QueueTargeted(
            FindActivePlayerConnection(playerId),
            new[] { EncodeEvent(serverEvent, "player:" + playerId) },
            signal: serverEvent.GetType().Name,
            cancellationToken));

    /// <summary>
    /// broadcast an event with per-connection interest gating (InteractionQueued actors off-scope skipped).
    /// </summary>
    public Task BroadcastEventInterestScopedAsync(ServerEvent serverEvent, CancellationToken cancellationToken) =>
        AwaitBestEffortAsync(QueueInterestScopedEvent(serverEvent, cancellationToken));

    internal Task SendEventToConnectionAsync(
        ClientConnection connection,
        ServerEvent serverEvent,
        CancellationToken cancellationToken) =>
        AwaitBestEffortAsync(QueueTargeted(
            connection,
            new[] { EncodeEvent(serverEvent, "connection", connection) },
            signal: serverEvent.GetType().Name,
            cancellationToken));

    internal bool ActivatePlayerConnection(ClientConnection connection, int playerId)
    {
        if (!_session.IsActiveConnectionLease(playerId, connection.ConnectionLease))
        {
            connection.MarkBroken("lease-expired");
            ScheduleConnectionDisposal(connection);
            return false;
        }

        List<ClientConnection> superseded = new();
        lock (_connectionsLock)
        {
            foreach (ClientConnection candidate in _connections.ToArray())
            {
                if (!ReferenceEquals(candidate, connection)
                    && candidate.AssociatedPlayerId == playerId)
                {
                    candidate.MarkSuperseded(connection);
                    _connections.Remove(candidate);
                    superseded.Add(candidate);
                }
            }
        }

        foreach (ClientConnection candidate in superseded)
        {
            ScheduleConnectionDisposal(candidate);
        }
        return true;
    }

    private OutboundEnqueueReceipt QueueBroadcast(
        IReadOnlyList<string> lines,
        string signal,
        bool requireRecipient,
        CancellationToken cancellationToken,
        ClientConnection? exclude = null)
    {
        ClientConnection[] connections = SnapshotActiveConnections();
        OutboundRecipient[] recipients = connections
            .Where(connection => !ReferenceEquals(connection, exclude))
            .Select(connection => new OutboundRecipient(connection, lines))
            .ToArray();
        return QueueRecipients(recipients, signal, requireRecipient, cancellationToken);
    }

    private OutboundEnqueueReceipt QueueTargeted(
        ClientConnection? target,
        IReadOnlyList<string> lines,
        string signal,
        CancellationToken cancellationToken)
    {
        OutboundRecipient[] recipients = target == null
            ? Array.Empty<OutboundRecipient>()
            : new[] { new OutboundRecipient(target, lines) };
        return QueueRecipients(recipients, signal, requireRecipient: false, cancellationToken);
    }

    private OutboundEnqueueReceipt QueueInterestScopedEvent(
        ServerEvent serverEvent,
        CancellationToken cancellationToken)
    {
        string line = EncodeEvent(serverEvent, "broadcast");
        List<OutboundRecipient> recipients = new();
        foreach (ClientConnection connection in SnapshotActiveConnections())
        {
            int playerId = connection.AssociatedPlayerId!.Value;
            if (serverEvent is ServerEvent.InteractionQueued interaction
                && !_session.ShouldBroadcastInteractionForPlayer(playerId, interaction))
            {
                continue;
            }

            if (serverEvent is ServerEvent.InteractionReplayQueued replay
                && !_session.ShouldBroadcastInteractionReplayForPlayer(playerId, replay))
            {
                continue;
            }

            recipients.Add(new OutboundRecipient(connection, new[] { line }));
        }
        return QueueRecipients(
            recipients,
            serverEvent.GetType().Name,
            requireRecipient: false,
            cancellationToken);
    }

    private OutboundEnqueueReceipt QueueRecipients(
        IReadOnlyList<OutboundRecipient> recipients,
        string signal,
        bool requireRecipient,
        CancellationToken cancellationToken)
    {
        JoinTrace.Send? traced = TraceSend(recipients, signal);
        OutboundEnqueueReceipt receipt = _outbound.TryEnqueue(
            signal,
            recipients.Count,
            requireRecipient,
            token => DeliverRecipientsAsync(recipients, signal, token, traced),
            cancellationToken,
            tag: recipients);
        if (!receipt.Accepted && traced != null)
        {
            traced.Refused(receipt.Completion.IsCompleted ? receipt.Completion.Result.FailureKind.ToString() : "not-accepted");
        }
        return receipt;
    }

    /// <summary>
    /// G14: a send queued on a traced join's flow, with the pump's state behind it: what is in flight and
    /// queued, and for each recipient what is pending for it. Null (and free) when nothing is traced.
    /// </summary>
    private JoinTrace.Send? TraceSend(IReadOnlyList<OutboundRecipient> recipients, string signal)
    {
        JoinTrace? trace = JoinTrace.Current;
        if (trace == null)
        {
            return null;
        }

        IReadOnlyList<OutboundDeliveryPump.PendingOperation> pending = _outbound.Pending();
        long now = Stopwatch.GetTimestamp();
        string[] labels = new string[recipients.Count];
        string[] pendingFor = new string[recipients.Count];
        long[] bytes = new long[recipients.Count];
        for (int i = 0; i < recipients.Count; i++)
        {
            ClientConnection connection = recipients[i].Connection;
            labels[i] = ReferenceEquals(connection, trace.Owner) ? "self" : connection.TraceLabel;
            pendingFor[i] = DescribePendingFor(pending, tag => tag is IReadOnlyList<OutboundRecipient> queued
                && queued.Any(other => ReferenceEquals(other.Connection, connection)), now);
            bytes[i] = recipients[i].Lines.Sum(static line => (long)Encoding.UTF8.GetByteCount(line) + 1);
        }
        return trace.AddSend(signal, DescribePump(pending, now), labels, pendingFor, bytes);
    }

    /// <summary>The pump as a new send finds it: "in-flight=motive_batch 3100 ms to=2 queued=3 oldest=dialog_request 2900 ms".</summary>
    internal static string DescribePump(IReadOnlyList<OutboundDeliveryPump.PendingOperation> pending, long now)
    {
        StringBuilder text = new();
        int queued = 0;
        OutboundDeliveryPump.PendingOperation? oldest = null;
        foreach (OutboundDeliveryPump.PendingOperation operation in pending)
        {
            if (operation.InFlight)
            {
                int to = operation.Tag is System.Collections.ICollection targets ? targets.Count : 0;
                text.Append(string.Create(CultureInfo.InvariantCulture,
                    $"in-flight={operation.Signal} {Stopwatch.GetElapsedTime(operation.StartedAt, now).TotalMilliseconds:F0} ms to={to} "));
                continue;
            }
            queued++;
            oldest ??= operation;
        }
        if (text.Length == 0)
        {
            text.Append("in-flight=none ");
        }
        text.Append(string.Create(CultureInfo.InvariantCulture, $"queued={queued}"));
        if (oldest is { } first)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $" oldest={first.Signal} {Stopwatch.GetElapsedTime(first.EnqueuedAt, now).TotalMilliseconds:F0} ms"));
        }
        return text.ToString();
    }

    /// <summary>What the pump holds for one recipient: "pending=2 oldest=motive_batch 3400 ms (in flight)", or "pending=0".</summary>
    internal static string DescribePendingFor(
        IReadOnlyList<OutboundDeliveryPump.PendingOperation> pending,
        Func<object?, bool> includes,
        long now)
    {
        int count = 0;
        OutboundDeliveryPump.PendingOperation? oldest = null;
        foreach (OutboundDeliveryPump.PendingOperation operation in pending)
        {
            if (includes(operation.Tag))
            {
                count++;
                oldest ??= operation;
            }
        }
        return oldest is { } first
            ? string.Create(CultureInfo.InvariantCulture,
                $"pending={count} oldest={first.Signal} {Stopwatch.GetElapsedTime(first.EnqueuedAt, now).TotalMilliseconds:F0} ms{(first.InFlight ? " (in flight)" : string.Empty)}")
            : "pending=0";
    }

    private async Task<OutboundDeliveryReceipt> DeliverRecipientsAsync(
        IReadOnlyList<OutboundRecipient> recipients,
        string signal,
        CancellationToken cancellationToken,
        JoinTrace.Send? traced = null)
    {
        traced?.Started();
        if (recipients.Count == 0)
        {
            traced?.Ended();
            return new OutboundDeliveryReceipt(0, 0, 0, OutboundFailureKind.ZeroRecipients);
        }

        bool[] delivered = await Task.WhenAll(recipients.Select(DeliverOneAsync));
        traced?.Ended();
        int deliveredCount = delivered.Count(static value => value);
        int failedCount = recipients.Count - deliveredCount;
        PruneBrokenConnections();
        OutboundFailureKind failureKind = failedCount switch
        {
            0 => OutboundFailureKind.None,
            _ when deliveredCount > 0 => OutboundFailureKind.PartialDelivery,
            _ => OutboundFailureKind.DeliveryFailed
        };
        return new OutboundDeliveryReceipt(
            recipients.Count,
            deliveredCount,
            failedCount,
            failureKind);

        async Task<bool> DeliverOneAsync(OutboundRecipient recipient, int index)
        {
            ClientConnection connection = recipient.Connection;
            if (connection.IsBroken)
            {
                traced?.Wrote(index, Stopwatch.GetTimestamp(), "broken");
                return false;
            }

            CancellationTokenSource? writeTimeout = null;
            long started = Stopwatch.GetTimestamp();
            long wroteBefore = connection.BytesOut;
            try
            {
                writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeTimeout.CancelAfter(_broadcastWriteTimeout);
                connection.NoteOp("send:" + signal);
                await connection.WriteLinesAsync(recipient.Lines, writeTimeout.Token);
                traced?.Wrote(index, started, "ok");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // G14: what was being written, how much of it went, and how long the write was pending.
                long total = recipient.Lines.Sum(static line => (long)Encoding.UTF8.GetByteCount(line) + 1);
                connection.MarkBroken(WriteFailureReason(
                        ex, writeTimeout?.IsCancellationRequested == true, cancellationToken.IsCancellationRequested)
                    + string.Create(CultureInfo.InvariantCulture,
                        $" ({signal}, {recipient.Lines.Count} line(s), wrote {connection.BytesOut - wroteBefore} of {total} B, pending {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)"));
                traced?.Wrote(index, started, "failed");
                return false;
            }
            finally
            {
                writeTimeout?.Dispose();
            }
        }
    }

    private ClientConnection[] SnapshotActiveConnections()
    {
        lock (_connectionsLock)
        {
            return _connections.Where(IsActiveConnection).ToArray();
        }
    }

    private ClientConnection? FindActivePlayerConnection(int playerId)
    {
        lock (_connectionsLock)
        {
            return _connections.FirstOrDefault(connection =>
                connection.AssociatedPlayerId == playerId
                && IsActiveConnection(connection));
        }
    }

    private bool IsActiveConnection(ClientConnection connection) =>
        !connection.IsBroken
        && connection.AssociatedPlayerId is int playerId
        && _session.IsActiveConnectionLease(playerId, connection.ConnectionLease);

    private async Task AwaitBestEffortAsync(OutboundEnqueueReceipt enqueueReceipt)
    {
        OutboundDeliveryReceipt receipt = await enqueueReceipt.Completion.ConfigureAwait(false);
        if (receipt.FailureKind == OutboundFailureKind.Cancelled)
        {
            throw new OperationCanceledException();
        }
        if (!enqueueReceipt.Accepted)
        {
            throw new InvalidOperationException(
                $"Outbound operation was not accepted: {receipt.FailureKind}.");
        }
    }

    private static async Task<bool> AwaitAllDeliveredAsync(OutboundEnqueueReceipt enqueueReceipt)
    {
        OutboundDeliveryReceipt receipt = await enqueueReceipt.Completion.ConfigureAwait(false);
        return enqueueReceipt.Accepted && receipt.AllDelivered;
    }

    private void ReportOutboundReceipt(
        OutboundDeliveryReceipt receipt,
        string signal,
        bool requireRecipient)
    {
        bool report = receipt.FailureKind switch
        {
            OutboundFailureKind.None or OutboundFailureKind.Cancelled => false,
            OutboundFailureKind.ZeroRecipients => requireRecipient,
            _ => true
        };
        if (!report)
        {
            return;
        }

        string message =
            $"Outbound {signal} failed: kind={receipt.FailureKind} " +
            $"intended={receipt.Intended} delivered={receipt.Delivered} failed={receipt.Failed}.";
        IOException exception = new(message);
        if (_failureReporter != null)
        {
            _failureReporter(exception, $"outbound.{signal}");
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    private sealed record OutboundRecipient(
        ClientConnection Connection,
        IReadOnlyList<string> Lines);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _shutdown.Cancel();
        }
        catch
        {
        }
        lock (_playerSidecars)
        {
            foreach (PlayerSidecar entry in _playerSidecars.Values)
            {
                try { entry.Sidecar?.Dispose(); } catch { /* already gone */ }
            }
            _playerSidecars.Clear();
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        }

        await _outbound.DisposeAsync();

        ClientConnection[] connections;
        lock (_connectionsLock)
        {
            connections = _connections.ToArray();
            _connections.Clear();
        }

        foreach (ClientConnection connection in connections)
        {
            connection.MarkBroken("shutdown");
            await connection.DisposeAsync();
        }

        _shutdown.Dispose();
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            ClientConnection connection = new(
                client,
                _session,
                this,
                linked.Token,
                _failureReporter,
                _transactionFactory,
                _maxCommandLineChars);
            lock (_connectionsLock)
            {
                _connections.Add(connection);
            }

            connection.Start();
        }
    }

    internal void RemoveConnection(ClientConnection connection)
    {
        lock (_connectionsLock)
        {
            _connections.Remove(connection);
        }
    }

    private void PruneBrokenConnections()
    {
        List<ClientConnection> toDispose = new();
        lock (_connectionsLock)
        {
            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                if (_connections[i].IsBroken)
                {
                    toDispose.Add(_connections[i]);
                    _connections.RemoveAt(i);
                }
            }
        }

        foreach (ClientConnection broken in toDispose)
        {
            ScheduleConnectionDisposal(broken);
        }
    }

    internal void ScheduleConnectionDisposal(ClientConnection connection)
    {
        _ = DisposeConnectionAsync(connection);

        async Task DisposeConnectionAsync(ClientConnection candidate)
        {
            try
            {
                await candidate.DisposeAsync();
            }
            catch (Exception ex)
            {
                _failureReporter?.Invoke(ex, "connection.dispose");
            }
        }
    }

    public static IPEndPoint ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Listener endpoint is empty.", nameof(endpoint));
        }

        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
        {
            throw new FormatException($"Listener endpoint '{endpoint}' is not in <host>:<port> form.");
        }

        string host = endpoint[..colon];
        string portText = endpoint[(colon + 1)..];
        // Port 0 is the standard "ask the OS for an ephemeral port" sentinel; TcpListener handles it natively.
        // Valid range is 0..65535. Negative is invalid.
        if (!int.TryParse(portText, out int port) || port < 0 || port > 65535)
        {
            throw new FormatException($"Listener endpoint port '{portText}' is not a valid TCP port.");
        }

        IPAddress address = host switch
        {
            "0.0.0.0" => IPAddress.Any,
            "localhost" or "127.0.0.1" => IPAddress.Loopback,
            _ => IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? parsed)
                ? parsed
                : throw new FormatException(
                    $"Listener host '{host}' is not an IP address. Use 127.0.0.1 (this machine only), a private address, or 0.0.0.0.")
        };

        return new IPEndPoint(address, port);
    }
}

internal sealed class ClientConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly AuthoritativeSession _session;
    private readonly ClientListenerServer _server;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<Exception, string>? _failureReporter;
    private readonly Func<ClientCommand, SimsOnlineTelemetryTransaction>? _transactionFactory;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly BoundedLineReader _lines;
    private readonly bool _isLoopbackPeer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Guid _connectionLease = Guid.NewGuid();
    private readonly object _disposeLock = new();
    private readonly IPEndPoint? _remote;
    private readonly long _openedTimestamp = Stopwatch.GetTimestamp();
    private long _bytesIn;
    private long _bytesOut;
    private volatile string _lastOp = "none";
    private string? _closeReason;
    private string? _supersededBy;
    private int? _loggedPlayerId;
    private Exception? _encodeFailure;
    private string? _encodeFailureEvent;
    private long _joinedAt;
    private bool _traceNextAck;
    private Task? _readLoop;
    private Task? _disposeTask;
    private volatile bool _disposeStarted;
    private volatile bool _broken;

    // P3.1: the first PlayerId we see on a successful JoinSession. On disconnect we synthesize
    // a LeaveSession for this player so their Sim claims are released. One-player-per-connection
    // simplification; multi-player-per-connection support can come later if needed.
    private int? _associatedPlayerId;

    // Wave 7 · the Rust authority sidecar seam (cards 31-33, SIMSONLINE_RUST_SIDECAR=1, default off).
    // The attachment itself lives on ClientListenerServer keyed by player (card 35 gate 11: a
    // reconnecting player reclaims through the SAME daemon player, so no invented ownership
    // conflicts); this connection only consults it, lazily on the first command it speaks.

    internal int? AssociatedPlayerId => _associatedPlayerId;

    internal Guid ConnectionLease => _connectionLease;

    /// <summary>The peer's address and port, kept from the accept (a closed socket no longer has one).</summary>
    internal string RemoteEndpointText => _remote?.ToString() ?? "unknown";

    /// <summary>Why this connection closed or is closing (the first reason recorded wins), or null while open.</summary>
    internal string? CloseReason => Volatile.Read(ref _closeReason);

    public ClientConnection(
        TcpClient client,
        AuthoritativeSession session,
        ClientListenerServer server,
        CancellationToken cancellationToken,
        Action<Exception, string>? failureReporter = null,
        Func<ClientCommand, SimsOnlineTelemetryTransaction>? transactionFactory = null,
        int maxCommandLineChars = ClientListenerServer.DefaultMaxCommandLineChars)
    {
        _client = client;
        _session = session;
        _server = server;
        _cancellationToken = cancellationToken;
        _failureReporter = failureReporter;
        _transactionFactory = transactionFactory;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        _lines = new BoundedLineReader(_reader, maxCommandLineChars);
        _remote = client.Client.RemoteEndPoint as IPEndPoint;
        _isLoopbackPeer = _remote != null && IsLoopback(_remote.Address);
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    public bool IsBroken => _broken || _disposeStarted;

    /// <summary>Closes the connection for writes, recording why (the first reason recorded wins).</summary>
    internal void MarkBroken(string reason)
    {
        NoteCloseReason(reason);
        _broken = true;
    }

    /// <summary>A newer connection joined as this connection's player: its remote endpoint goes in the close line.</summary>
    internal void MarkSuperseded(ClientConnection by)
    {
        if (Interlocked.CompareExchange(ref _supersededBy, by.RemoteEndpointText, null) == null)
        {
            MarkBroken("superseded-by " + (by._remote?.Port.ToString(CultureInfo.InvariantCulture) ?? "unknown"));
        }
    }

    internal void NoteOp(string op) => _lastOp = op;

    internal long BytesOut => Interlocked.Read(ref _bytesOut);

    /// <summary>An event bound for this connection that the codec refused: its throw ends the read loop.</summary>
    internal void NoteEncodeFailure(string eventName, Exception ex)
    {
        _encodeFailureEvent = eventName;
        _encodeFailure = ex;
        NoteOp("encode:" + eventName);
    }

    private void NoteCloseReason(string reason) => Interlocked.CompareExchange(ref _closeReason, reason, null);

    /// <summary>A socket error's name (ConnectionReset, ConnectionAborted, ...), else the exception's type.</summary>
    internal static string ErrorCode(Exception ex) =>
        (ex as SocketException ?? ex.InnerException as SocketException)?.SocketErrorCode.ToString() ?? ex.GetType().Name;

    /// <summary>An exception's type and message on one line, the message cut at 240 characters.</summary>
    internal static string Describe(Exception ex)
    {
        string message = ex.Message.ReplaceLineEndings(" ");
        return ex.GetType().Name + ": " + (message.Length > 240 ? message[..240] + "..." : message);
    }

    internal static string UtcStamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private string PlayerText => _loggedPlayerId?.ToString(CultureInfo.InvariantCulture) ?? "none";

    /// <summary> G14: this connection as a send's recipient in a join trace: "player:12@127.0.0.1:50211".</summary>
    internal string TraceLabel => (_associatedPlayerId is int player
        ? "player:" + player.ToString(CultureInfo.InvariantCulture)
        : "unjoined") + "@" + RemoteEndpointText;

    public void Start()
    {
        _readLoop = Task.Run(RunReadLoopAsync, CancellationToken.None);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await WriteLinesAsync(new[] { line }, cancellationToken);
    }

    public async Task WriteLinesAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        if (IsBroken)
        {
            throw new IOException("Connection is no longer writable.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (IsBroken)
            {
                throw new IOException("Connection is no longer writable.");
            }

            foreach (string line in lines)
            {
                byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(payload, cancellationToken);
                Interlocked.Add(ref _bytesOut, payload.Length);
            }
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeLock)
        {
            _disposeStarted = true;
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await CloseAsync();
        }
        finally
        {
            LogClosed(); // also when the read loop faulted (its exception still reaches the dispose's caller)
        }
    }

    private async Task CloseAsync()
    {
        MarkBroken("disposed");

        try
        {
            _reader.Dispose();
        }
        catch
        {
        }

        try
        {
            _client.Close();
        }
        catch
        {
        }

        if (_readLoop != null)
        {
            try
            {
                await _readLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        await _writeLock.WaitAsync(CancellationToken.None);
        _writeLock.Release();
        _writeLock.Dispose();
    }

    private void LogClosed()
    {
        try
        {
            string age = Stopwatch.GetElapsedTime(_openedTimestamp).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            string by = _supersededBy == null ? string.Empty : " by=" + _supersededBy;
            _server.LogConnection(
                $"[client-conn] closed utc={UtcStamp()} player={PlayerText} remote={RemoteEndpointText} age={age}s "
                + $"in={Interlocked.Read(ref _bytesIn)}B out={Interlocked.Read(ref _bytesOut)}B last={_lastOp}{by} "
                + $"reason={CloseReason ?? "unknown"}");
        }
        catch
        {
            // The close line must never fail a disposal.
        }
    }

    private async Task RunReadLoopAsync()
    {
        try
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _lines.ReadLineAsync(_cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    NoteCloseReason("shutdown");
                    return;
                }
                catch (LineTooLongException tooLong)
                {
                    await SafeWriteEventAsync(new ServerEvent.ServerError(tooLong.Message + " Closing the connection."));
                    MarkBroken("line-too-long");
                    return;
                }
                catch (IOException ex)
                {
                    MarkBroken("read-error " + ErrorCode(ex));
                    return;
                }
                catch (ObjectDisposedException)
                {
                    NoteCloseReason("disposed");
                    return;
                }

                if (line == null)
                {
                    NoteCloseReason("normal");
                    return;
                }

                Interlocked.Add(ref _bytesIn, Encoding.UTF8.GetByteCount(line) + 1);

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    await HandleLineAsync(line);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // G14: a throw out of a command's handling ends the read loop, and so the connection, as
                    // before; the close line names it (its last= is the operation it was on).
                    NoteCloseReason(ReferenceEquals(ex, _encodeFailure)
                        ? $"encode-throw {_encodeFailureEvent} {Describe(ex)}"
                        : "handler-error " + Describe(ex));
                    throw;
                }
            }
        }
        finally
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                NoteCloseReason("shutdown");
            }

            // P3.1: if the client disconnected without sending LeaveSession, synthesize one
            // here so their Sim claims are released and broadcast to remaining clients.
            if (_associatedPlayerId.HasValue)
            {
                int playerId = _associatedPlayerId.Value;
                _associatedPlayerId = null;
                try
                {
                    CommandResult? leaveResult = await _session.ReleasePlayerClaimsAndLeaveAsync(
                        playerId,
                        _connectionLease,
                        CancellationToken.None);
                    if (leaveResult != null)
                    {
                        foreach (ServerEvent evt in leaveResult.Events)
                        {
                            if (IsBroadcastEvent(evt))
                            {
                                try
                                {
                                    await _server.BroadcastEventAsync(evt, CancellationToken.None);
                                }
                                catch
                                {
                                    // Cleanup-time broadcast failures are non-fatal.
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Cleanup must never throw out of the read loop.
                }
            }

            _server.RemoveConnection(this);
            _server.ScheduleConnectionDisposal(this);
        }
    }

    private async Task HandleLineAsync(string line)
    {
        if (!PublicProtocolCodec.TryDecodeCommand(line, out ClientCommand? command, out string? decodeError) || command == null)
        {
            NoteOp("recv:malformed");
            // Card 33 §C (follow-up forensics §4): a refusal must be named AND seen. A malformed
            // line can still carry the correlation key the client is waiting on, so recover the
            // commandId when it is parseable and answer CommandRejected; only a line with no
            // recoverable id falls back to the bare ServerError.
            Guid? orphanedId = TryExtractCommandId(line);
            if (orphanedId is Guid lostId)
            {
                await SafeWriteEventAsync(new ServerEvent.CommandRejected(
                    lostId, "Malformed client command: " + (decodeError ?? "undecodable line.")));
            }
            else
            {
                await SafeWriteEventAsync(new ServerEvent.ServerError(decodeError ?? "Malformed client command."));
            }
            return;
        }

        if (command is ClientCommand.JoinSession && _associatedPlayerId.HasValue)
        {
            await SafeWriteEventAsync(new ServerEvent.CommandRejected(
                command.CommandId,
                "This connection is already joined to a player session."));
            return;
        }

        NoteOp("recv:" + command.GetType().Name);
        JoinTrace? trace = BeginTrace(command);
        if (trace == null)
        {
            await HandleCommandAsync(command, null);
            return;
        }

        using JoinTrace.Scope traced = JoinTrace.Enter(trace);
        try
        {
            await HandleCommandAsync(command, trace);
        }
        catch (Exception ex)
        {
            trace.Outcome = "error " + Describe(ex);
            throw;
        }
        finally
        {
            trace.Emit(_server.LogConnection);
        }
    }

    /// <summary> G14: a join, and the first snapshot ack after it, are traced (one block each).</summary>
    private JoinTrace? BeginTrace(ClientCommand command)
    {
        switch (command)
        {
            case ClientCommand.JoinSession join:
                return new JoinTrace(
                    "player=" + join.PlayerId.ToString(CultureInfo.InvariantCulture),
                    "JoinSession remote=" + RemoteEndpointText,
                    owner: this);
            case ClientCommand.AckSnapshot ack when _traceNextAck:
                _traceNextAck = false;
                return new JoinTrace(
                    "player=" + PlayerText,
                    string.Create(CultureInfo.InvariantCulture,
                        $"AckSnapshot tick={ack.Tick} since-join={Stopwatch.GetElapsedTime(_joinedAt).TotalMilliseconds:F0} ms"),
                    owner: this);
            default:
                return null;
        }
    }

    private async Task HandleCommandAsync(ClientCommand command, JoinTrace? trace)
    {
        string? refusal = CheckCommandAuthority(command, _associatedPlayerId, _isLoopbackPeer);
        if (refusal != null)
        {
            if (trace != null)
            {
                trace.Outcome = "refused " + refusal;
            }
            await SafeWriteEventAsync(new ServerEvent.CommandRejected(command.CommandId, refusal));
            return;
        }

        // Wave 7 · cards 31-33: the sidecar seam is consulted only after CheckCommandAuthority has
        // passed — the SECURITY.md order is unchanged — and its named rejections are relayed verbatim.
        string? sidecarRefusal = ConsultAuthoritySidecar(command);
        if (sidecarRefusal != null)
        {
            if (trace != null)
            {
                trace.Outcome = "sidecar-refused " + sidecarRefusal;
            }
            await SafeWriteEventAsync(new ServerEvent.CommandRejected(command.CommandId, sidecarRefusal));
            return;
        }

        using SimsOnlineTelemetryTransaction? transaction = _transactionFactory?.Invoke(command);
        CommandResult result;
        long handling = JoinTrace.Now();
        try
        {
            result = await _session.HandleCommandAsync(command, _cancellationToken, _connectionLease);
        }
        catch (OperationCanceledException)
        {
            if (trace != null)
            {
                trace.Outcome = "cancelled";
            }
            return;
        }
        catch (Exception ex)
        {
            if (trace != null)
            {
                trace.Outcome = "handler threw " + Describe(ex);
            }
            _failureReporter?.Invoke(ex, command.GetType().Name);
            transaction?.Complete(succeeded: false);
            await SafeWriteEventAsync(new ServerEvent.CommandRejected(command.CommandId, ex.Message));
            await SafeWriteEventAsync(new ServerEvent.ServerError(ex.Message));
            return;
        }

        transaction?.Complete(result.IsAccepted);
        if (trace != null)
        {
            trace.Step("handle", handling, string.Create(CultureInfo.InvariantCulture, $"events={result.Events.Count}"));
            trace.Outcome = result.IsAccepted ? "accepted" : "rejected " + result.RejectionReason;
        }

        if (result.IsAccepted)
        {
            Task acceptedWrite = _server.SendEventToConnectionAsync(
                this,
                new ServerEvent.CommandAccepted(command.CommandId),
                _cancellationToken);

            if (command is ClientCommand.JoinSession join && _associatedPlayerId == null)
            {
                _associatedPlayerId = join.PlayerId;
                _loggedPlayerId = join.PlayerId;
                long activating = JoinTrace.Now();
                bool activated = _server.ActivatePlayerConnection(this, join.PlayerId);
                trace?.Step("activate", activating, activated ? null : "lease-expired");
                if (!activated)
                {
                    await AwaitSafeWriteAsync(acceptedWrite, nameof(ServerEvent.CommandAccepted));
                    return;
                }
                _joinedAt = Stopwatch.GetTimestamp();
                _traceNextAck = true;
            }
            else if (command is ClientCommand.LeaveSession leave && _associatedPlayerId == leave.PlayerId)
            {
                _associatedPlayerId = null;
            }

            await AwaitSafeWriteAsync(acceptedWrite, nameof(ServerEvent.CommandAccepted));
        }
        else
        {
            await SafeWriteEventAsync(new ServerEvent.CommandRejected(command.CommandId, result.RejectionReason ?? "Rejected by session."));
        }

        // Event routing: SimClaimed / SimReleased / SessionJoined are session-wide state changes
        // that every connected client needs to see (e.g. another player needs to know a Sim was
        // claimed so their UI grays out the claim button). Everything else is a reply for the
        // sending client only. Broadcast-class events always go to the SENDER directly too: the
        // fan-out only targets joined (active) connections, so an unjoined admin connection
        // issuing the command would otherwise never observe the outcome (simctl speed hang).
        foreach (ServerEvent evt in result.Events)
        {
            if (IsBroadcastEvent(evt))
            {
                try
                {
                    await _server.BroadcastEventAsync(evt, exclude: this, _cancellationToken);
                    await SafeWriteEventAsync(evt);
                }
                catch
                {
                    // Broadcast failures (e.g. linked CTS already cancelled) fall back to writing
                    // the event to the sender so the originator at least sees the outcome.
                    await SafeWriteEventAsync(evt);
                }
            }
            else
            {
                await SafeWriteEventAsync(evt);
            }
        }
    }

    /// <summary>
    /// Who may send what on a connection; null when the command may proceed, else the rejection reason.
    /// A joined connection speaks only for its own player. Before joining, a remote peer may join and make
    /// read-only requests (a client downloads the world on a separate, unjoined connection). Loopback
    /// connections are the local admin path: scripts/simctl.py never joins and commands any player.
    /// </summary>
    internal static string? CheckCommandAuthority(ClientCommand command, int? joinedPlayerId, bool isLoopbackPeer)
    {
        if (joinedPlayerId is int joined)
        {
            return command.PlayerId == joined
                ? null
                : $"This connection is joined as player {joined}; it cannot send commands for player {command.PlayerId}.";
        }

        if (isLoopbackPeer || command is ClientCommand.JoinSession or ClientCommand.RequestWorld
                or ClientCommand.RequestSnapshot or ClientCommand.RequestSimDescription)
        {
            return null;
        }

        return "Join the session first: before joining, a remote connection may only send join_session and "
            + "read-only requests.";
    }

    /// <summary>
    /// The commandId of a line that failed polymorphic decode (a truncated JSON body or an unknown
    /// <c>$type</c> still carries the field textually). Bounded input (<see cref="BoundedLineReader"/>)
    /// and a single anchored scan keep this error-path-only cheap.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CommandIdPattern = new(
        "\"commandId\"\\s*:\\s*\"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\"",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static Guid? TryExtractCommandId(string line)
    {
        var match = CommandIdPattern.Match(line);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out Guid id) ? id : null;
    }

    /// <summary>
    /// Wave 7 · the card 31-33 authority seam, consulted only after <see cref="CheckCommandAuthority"/>.
    /// A joining player first meets the daemon's card-19 hello gate (CLIENT_ENV attestation): a
    /// refusal there blocks join_session and request_world with the kernel's named reason before any
    /// chunk byte moves (card 32 §D). request_world is consulted before the native answer (fingerprint
    /// reuse vs checkpoint, the eleven WFC chunk frames, the read-only primary pin, the SIMC
    /// claim replay, HISTRESET) and the verdict is recorded. Each kernel-owned public verb is
    /// forwarded under the command's own identity (card 33 §D.2 + card 35 gate 11: the sidecar
    /// maps CommandId -> corr, so a retried command — across a reconnect too, the player's daemon
    /// attachment outlives the connection — replays the ORIGINAL result, never a second effect).
    /// ACK + authoritative
    /// delta frames are relayed to the server log frame by frame — never silently dropped — and an
    /// <c>ERR</c> naming a card-33 §C KernelError is relayed verbatim and blocks the native path.
    /// The seam is additive (card 31 §C): when disabled, when the daemon is down, or when the kernel
    /// answers with something it has no structured opinion on, the command proceeds natively.
    /// Consults run on this connection's read loop with the bounded sidecar timeout, never on the
    /// tick pump, mirroring the daemon's dropped_ticks_during_join == 0 invariant (card 32 §C.1).
    /// </summary>
    private string? ConsultAuthoritySidecar(ClientCommand command)
    {
        if (!RustAuthoritySidecar.Enabled)
        {
            return null;
        }

        ClientListenerServer.PlayerSidecar entry = _server.AcquirePlayerSidecar(command.PlayerId, RemoteEndpointText);

        if (entry.HelloRejection is not null
            && command is ClientCommand.JoinSession or ClientCommand.RequestWorld)
        {
            return "rust authority sidecar: " + entry.HelloRejection;
        }

        if (entry.Sidecar is null)
        {
            return null; // transport down (logged) — the seam is off, use the native answer
        }

        try
        {
            lock (entry.Gate)
            {
                if (command is ClientCommand.RequestWorld)
                {
                    SidecarJoinBootstrap bootstrap = SidecarJoinBootstrap.Parse(entry.Sidecar.RequestWorldFor(command.CommandId));
                    _server.LogConnection($"[sidecar] request-world player={PlayerText} reuse={bootstrap.Reuse} "
                        + $"checkpoint={(bootstrap.Checkpoint is not null)} wfc={bootstrap.ChunkFrames}/{SidecarJoinBootstrap.ExpectedChunks} "
                        + $"primary={bootstrap.PrimaryPin ?? "none"} simc={bootstrap.ClaimReplayFrames} "
                        + $"histreset={bootstrap.HistoryReset} complete={bootstrap.IsComplete}");
                    return null; // the fingerprint verdict is recorded; the controller streams its bytes as today
                }

                if (RustAuthorityForward.TryBuild(command) is not (string verb, object[] args))
                {
                    return null; // no kernel verb for this command — native answer
                }

                SidecarVerdict verdict = entry.Sidecar.ForwardCommand(command.CommandId, verb, args);
                foreach (string frame in verdict.Frames)
                {
                    _server.LogConnection($"[sidecar] player={PlayerText} verb={verb} frame={frame}"); // relayed, never swallowed
                }

                return verdict.IsAuthoritativeRejection
                    ? "rust authority sidecar: " + verdict.RejectionText
                    : null;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException
                                   or ObjectDisposedException or OperationCanceledException)
        {
            _server.LogConnection($"[sidecar] consult failed player={PlayerText} command={command.GetType().Name} "
                + $"error={Describe(ex)}; seam inert for this command, native answer");
            _server.NoteSidecarTransportDead(command.PlayerId); // a restarted daemon becomes reachable again
            return null; // a transport error is never an authority verdict
        }
    }

    private static bool IsBroadcastEvent(ServerEvent evt) =>
        evt is ServerEvent.SimClaimed
            or ServerEvent.SimReleased
            or ServerEvent.SimAutonomyChanged
            or ServerEvent.SessionJoined
            or ServerEvent.GameSpeedChanged
            or ServerEvent.WorldTimeChanged
            or ServerEvent.SaveCheckpointed
            or ServerEvent.LotReconcileAcknowledged;

    private async Task SafeWriteEventAsync(ServerEvent serverEvent)
    {
        await AwaitSafeWriteAsync(_server.SendEventToConnectionAsync(
            this,
            serverEvent,
            _cancellationToken), serverEvent.GetType().Name);
    }

    private async Task AwaitSafeWriteAsync(Task writeTask, string what)
    {
        try
        {
            await writeTask;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                MarkBroken("shutdown");
                return;
            }

            // G14: the write a reply could not make, logged before the connection is marked broken.
            _server.LogConnection($"[client-conn] write failed utc={UtcStamp()} player={PlayerText} remote={RemoteEndpointText} "
                + $"event={what} error={Describe(ex)}");
            MarkBroken($"send-failed {what} {Describe(ex)}");
        }
    }
}
