using global::Sentry;

namespace SimsOnline.DedicatedServer.Observability;

public sealed class SimsOnlineTelemetryTransaction : IDisposable
{
    internal static SimsOnlineTelemetryTransaction Disabled { get; } = new(null, null);

    private readonly ITransactionTracer? _transaction;
    private readonly IDisposable? _scopeLifetime;
    private bool _finished;

    internal SimsOnlineTelemetryTransaction(
        ITransactionTracer? transaction,
        IDisposable? scopeLifetime)
    {
        _transaction = transaction;
        _scopeLifetime = scopeLifetime;
    }

    public SimsOnlineTelemetrySpan StartChild(string operation, string description)
    {
        if (_transaction == null || _finished)
        {
            return SimsOnlineTelemetrySpan.Disabled;
        }

        return new SimsOnlineTelemetrySpan(_transaction.StartChild(operation, description));
    }

    public void Complete(bool succeeded)
    {
        if (_transaction == null || _finished)
        {
            return;
        }

        _transaction.Finish(succeeded ? SpanStatus.Ok : SpanStatus.InternalError);
        _finished = true;
        _scopeLifetime?.Dispose();
    }

    public void Dispose()
    {
        if (_transaction != null && !_finished)
        {
            _transaction.Finish(SpanStatus.Cancelled);
            _finished = true;
            _scopeLifetime?.Dispose();
        }
    }
}

public sealed class SimsOnlineTelemetrySpan : IDisposable
{
    internal static SimsOnlineTelemetrySpan Disabled { get; } = new(null);

    private readonly ISpan? _span;
    private bool _finished;

    internal SimsOnlineTelemetrySpan(ISpan? span)
    {
        _span = span;
    }

    public void Complete(bool succeeded)
    {
        if (_span == null || _finished)
        {
            return;
        }

        _span.Finish(succeeded ? SpanStatus.Ok : SpanStatus.InternalError);
        _finished = true;
    }

    public void Dispose()
    {
        if (_span != null && !_finished)
        {
            _span.Finish(SpanStatus.Cancelled);
            _finished = true;
        }
    }
}
