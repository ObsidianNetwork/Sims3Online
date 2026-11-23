using global::Sentry;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Observability;

public sealed partial class SimsOnlineTelemetry
{
    public void AttachTrace(ClientCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsEnabled)
        {
            return;
        }

        command.SentryTrace = SentrySdk.GetTraceHeader()?.ToString();
        command.SentryBaggage = SentrySdk.GetBaggage()?.ToString();
    }

    public SimsOnlineTelemetryTransaction StartTransaction(string name, string operation) =>
        StartTransaction(name, operation, sentryTrace: null, sentryBaggage: null);

    public SimsOnlineTelemetryTransaction StartTransaction(
        string name,
        string operation,
        string? sentryTrace,
        string? sentryBaggage)
    {
        if (!IsEnabled)
        {
            return SimsOnlineTelemetryTransaction.Disabled;
        }

        IDisposable scopeLifetime = SentrySdk.PushScope();
        ITransactionTracer transaction = SentrySdk.StartTransaction(
            CreateTransactionContext(name, operation, sentryTrace, sentryBaggage));
        transaction.SetTag("signal", operation);
        SentrySdk.ConfigureScope(scope => scope.Transaction = transaction);
        return new SimsOnlineTelemetryTransaction(transaction, scopeLifetime);
    }

    private static ITransactionContext CreateTransactionContext(
        string name,
        string operation,
        string? sentryTrace,
        string? sentryBaggage)
    {
        _ = sentryBaggage;
        if (string.IsNullOrWhiteSpace(sentryTrace) || sentryTrace.Length > 128)
        {
            return new TransactionContext(name, operation);
        }

        try
        {
            SentryTraceHeader? parsed = SentryTraceHeader.Parse(sentryTrace);
            return parsed == null
                ? new TransactionContext(name, operation)
                : new TransactionContext(
                    name,
                    operation,
                    parentSpanId: parsed.SpanId,
                    traceId: parsed.TraceId,
                    isSampled: null,
                    isParentSampled: null);
        }
        catch (FormatException)
        {
            return new TransactionContext(name, operation);
        }
    }
}
