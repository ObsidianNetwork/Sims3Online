using System.Diagnostics;
using System.Threading.Channels;

namespace SimsOnline.DedicatedServer.Network;

internal enum OutboundFailureKind
{
    None,
    ZeroRecipients,
    QueueFull,
    Disposed,
    Cancelled,
    PartialDelivery,
    DeliveryFailed,
    Faulted
}

internal readonly record struct OutboundDeliveryReceipt(
    int Intended,
    int Delivered,
    int Failed,
    OutboundFailureKind FailureKind)
{
    public bool AllDelivered =>
        Intended > 0
        && Delivered == Intended
        && Failed == 0
        && FailureKind == OutboundFailureKind.None;
}

internal readonly record struct OutboundEnqueueReceipt(
    bool Accepted,
    Task<OutboundDeliveryReceipt> Completion);

internal sealed class OutboundDeliveryPump : IAsyncDisposable
{
    private readonly Channel<OutboundOperation> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action<OutboundDeliveryReceipt, string, bool> _receiptReporter;
    private readonly Task _runner;
    private readonly object _pendingLock = new();
    private readonly LinkedList<OutboundOperation> _pending = new();
    private OutboundOperation? _inFlight;
    private long _inFlightStarted;
    private int _disposed;

    public OutboundDeliveryPump(
        int capacity,
        Action<OutboundDeliveryReceipt, string, bool> receiptReporter)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Outbound queue capacity must be positive.");
        }

        _receiptReporter = receiptReporter ?? throw new ArgumentNullException(nameof(receiptReporter));
        _channel = Channel.CreateBounded<OutboundOperation>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _runner = Task.Run(RunAsync, CancellationToken.None);
    }

    public OutboundEnqueueReceipt TryEnqueue(
        string signal,
        int intendedRecipients,
        bool requireRecipient,
        Func<CancellationToken, Task<OutboundDeliveryReceipt>> deliverAsync,
        CancellationToken cancellationToken,
        object? tag = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Rejected(OutboundFailureKind.Cancelled);
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return Rejected(OutboundFailureKind.Disposed);
        }

        TaskCompletionSource<OutboundDeliveryReceipt> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        OutboundOperation operation = new(
            signal,
            intendedRecipients,
            requireRecipient,
            deliverAsync,
            cancellationToken,
            completion,
            tag,
            Stopwatch.GetTimestamp());
        bool written;
        lock (_pendingLock)
        {
            written = _channel.Writer.TryWrite(operation);
            if (written)
            {
                operation.Node = _pending.AddLast(operation);
            }
        }
        if (written)
        {
            return new OutboundEnqueueReceipt(true, completion.Task);
        }

        OutboundFailureKind failureKind = Volatile.Read(ref _disposed) != 0
            ? OutboundFailureKind.Disposed
            : OutboundFailureKind.QueueFull;
        OutboundDeliveryReceipt receipt = FailureReceipt(intendedRecipients, failureKind);
        _receiptReporter(receipt, signal, requireRecipient);
        completion.TrySetResult(receipt);
        return new OutboundEnqueueReceipt(false, completion.Task);

        OutboundEnqueueReceipt Rejected(OutboundFailureKind failureKind)
        {
            OutboundDeliveryReceipt receipt = FailureReceipt(intendedRecipients, failureKind);
            if (failureKind != OutboundFailureKind.Cancelled)
            {
                _receiptReporter(receipt, signal, requireRecipient);
            }
            return new OutboundEnqueueReceipt(false, Task.FromResult(receipt));
        }
    }

    /// <summary>
    /// G14: what the pump holds now: the operation being delivered (first, with its start) and the queued
    /// ones, oldest first. The pump is one FIFO for every connection, so everything here is ahead of a new send.
    /// </summary>
    public IReadOnlyList<PendingOperation> Pending()
    {
        lock (_pendingLock)
        {
            List<PendingOperation> pending = new(_pending.Count + 1);
            if (_inFlight is { } current)
            {
                pending.Add(new PendingOperation(current.Signal, current.EnqueuedAt, _inFlightStarted, current.Tag));
            }
            foreach (OutboundOperation queued in _pending)
            {
                pending.Add(new PendingOperation(queued.Signal, queued.EnqueuedAt, 0, queued.Tag));
            }
            return pending;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _runner.ConfigureAwait(false);
            return;
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        await _runner.ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (OutboundOperation operation in _channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                lock (_pendingLock)
                {
                    Unlink(operation);
                    _inFlight = operation;
                    _inFlightStarted = Stopwatch.GetTimestamp();
                }
                await CompleteOperationAsync(operation).ConfigureAwait(false);
                lock (_pendingLock)
                {
                    _inFlight = null;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Clear();
                _inFlight = null;
            }
            while (_channel.Reader.TryRead(out OutboundOperation? pending))
            {
                pending.Completion.TrySetResult(
                    FailureReceipt(pending.IntendedRecipients, OutboundFailureKind.Cancelled));
            }
        }
    }

    private async Task CompleteOperationAsync(OutboundOperation operation)
    {
        OutboundDeliveryReceipt receipt;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdown.Token,
                operation.CancellationToken);
            receipt = await operation.DeliverAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            receipt = FailureReceipt(operation.IntendedRecipients, OutboundFailureKind.Cancelled);
        }
        catch
        {
            receipt = FailureReceipt(operation.IntendedRecipients, OutboundFailureKind.Faulted);
        }

        _receiptReporter(receipt, operation.Signal, operation.RequireRecipient);
        operation.Completion.TrySetResult(receipt);
    }

    private static OutboundDeliveryReceipt FailureReceipt(int intended, OutboundFailureKind failureKind) =>
        new(intended, 0, intended, failureKind);

    private void Unlink(OutboundOperation operation)
    {
        if (operation.Node is { List: not null } node)
        {
            _pending.Remove(node);
        }
        operation.Node = null;
    }

    /// <summary>One operation the pump holds. StartedAt is 0 while queued; Tag is what the enqueuer passed.</summary>
    internal readonly record struct PendingOperation(string Signal, long EnqueuedAt, long StartedAt, object? Tag)
    {
        public bool InFlight => StartedAt != 0;
    }

    private sealed record OutboundOperation(
        string Signal,
        int IntendedRecipients,
        bool RequireRecipient,
        Func<CancellationToken, Task<OutboundDeliveryReceipt>> DeliverAsync,
        CancellationToken CancellationToken,
        TaskCompletionSource<OutboundDeliveryReceipt> Completion,
        object? Tag,
        long EnqueuedAt)
    {
        public LinkedListNode<OutboundOperation>? Node { get; set; }
    }
}
