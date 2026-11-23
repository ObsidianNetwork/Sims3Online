using global::Sentry;
using global::Sentry.Extensibility;
using System.Collections.Concurrent;

namespace SimsOnline.DedicatedServer.Observability;

public sealed partial class SimsOnlineTelemetry : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// static accessor so deep code paths (HostBridgeServer, SyncBroadcaster) can
    /// emit named failure-class events without threading the instance through constructors.
    /// Set by <see cref="Start"/>, cleared by <see cref="Dispose"/>.
    /// </summary>
    private static SimsOnlineTelemetry? s_current;
    
    /// <summary>Static accessor for the current telemetry instance, or null if not started.</summary>
    public static SimsOnlineTelemetry? Current => s_current;

    private readonly IDisposable? _sdkLifetime;
    private readonly SimsOnlineTelemetryOptions _options;
    private readonly ConcurrentDictionary<string, byte> _reportedFailureSignals = new(StringComparer.Ordinal);
    private int _ts3ProcessId;
    private bool _disposed;

    private SimsOnlineTelemetry(IDisposable? sdkLifetime, SimsOnlineTelemetryOptions options)
    {
        _sdkLifetime = sdkLifetime;
        _options = options;
    }

    public bool IsEnabled => _sdkLifetime != null && !_disposed;

    public string SessionId => _options.SessionId;

    public void SetTs3ProcessId(int processId)
    {
        if (!IsEnabled || processId <= 0)
        {
            return;
        }

        Volatile.Write(ref _ts3ProcessId, processId);
        SentrySdk.ConfigureScope(scope => scope.SetTag(
            "ts3_pid",
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static SimsOnlineTelemetry Start(
        SimsOnlineTelemetryOptions options,
        ITransport? transport = null)
    {
        if (string.IsNullOrWhiteSpace(options.Dsn))
        {
            var noOp = new SimsOnlineTelemetry(null, options);
            s_current = noOp;
            return noOp;
        }

        IDisposable lifetime = SentrySdk.Init(sentryOptions =>
            ApplyOptions(sentryOptions, options, transport));
        ApplyScope(options);
        var instance = new SimsOnlineTelemetry(lifetime, options);
        s_current = instance;
        return instance;
    }

    public void AddBreadcrumb(
        string signal,
        IReadOnlyDictionary<string, string?>? attributes = null,
        BreadcrumbLevel level = BreadcrumbLevel.Info)
    {
        if (!IsEnabled)
        {
            return;
        }

        SentrySdk.AddBreadcrumb(
            signal,
            "simsonline.lifecycle",
            "state",
            SafeStringAttributes(attributes),
            level);
    }

    public SentryId CaptureException(
        Exception exception,
        string signal,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        if (!IsEnabled || exception is OperationCanceledException)
        {
            return SentryId.Empty;
        }

        return SentrySdk.CaptureException(exception, scope =>
        {
            scope.Level = SentryLevel.Error;
            scope.SetTag("signal", signal);
            ApplyScopeAttributes(scope, attributes);
        });
    }

    public SentryId ReportFailure(
        Exception exception,
        string signal,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        SentryId eventId = CaptureException(exception, signal, attributes);
        if (eventId == SentryId.Empty)
        {
            return eventId;
        }

        return eventId;
    }

    public SentryId ReportFailureOnce(
        Exception exception,
        string signal,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        string failureKey = BuildFailureKey(exception, signal, attributes);
        if (!IsEnabled || exception is OperationCanceledException
            || !_reportedFailureSignals.TryAdd(failureKey, 0))
        {
            return SentryId.Empty;
        }

        return ReportFailure(exception, signal, attributes);
    }

    public void LogWarning(
        string signal,
        IReadOnlyDictionary<string, object?>? attributes = null) =>
        Log(SentryLogLevel.Warning, signal, attributes);

    public void LogInfo(
        string signal,
        IReadOnlyDictionary<string, object?>? attributes = null) =>
        Log(SentryLogLevel.Info, signal, attributes);

    public void LogError(
        string signal,
        IReadOnlyDictionary<string, object?>? attributes = null) =>
        Log(SentryLogLevel.Error, signal, attributes);

    public void RecordRuntimeActiveMetric(bool active)
    {
        if (!IsEnabled || !_options.EnableMetrics)
        {
            return;
        }

        IEnumerable<KeyValuePair<string, object>> attributes =
            BaseObjectAttributes("runtime.active").ToArray();
        SentrySdk.Metrics.EmitGauge(
            "simsonline.runtime.active",
            active ? 1 : 0,
            MeasurementUnit.None,
            attributes,
            scope: null);
    }

    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        await SentrySdk.FlushAsync(timeout ?? DefaultFlushTimeout).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            RecordRuntimeActiveMetric(active: false);
            FlushAsync(DefaultFlushTimeout).GetAwaiter().GetResult();
        }
        finally
        {
            _disposed = true;
            _sdkLifetime?.Dispose();
            s_current = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            RecordRuntimeActiveMetric(active: false);
            await FlushAsync(DefaultFlushTimeout).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            _sdkLifetime?.Dispose();
            s_current = null;
        }
    }


    private void Log(
        SentryLogLevel level,
        string signal,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        if (!IsEnabled)
        {
            return;
        }

        void ConfigureLog(SentryLog log)
        {
            foreach (KeyValuePair<string, object> attribute in BaseObjectAttributes(signal))
            {
                log.SetAttribute(attribute.Key, attribute.Value);
            }

            if (attributes == null)
            {
                return;
            }

            foreach ((string key, object? value) in attributes)
            {
                if (value != null
                    && !SentryPrivacyFilter.ShouldStripTag(key)
                    && TryNormalizeLogAttribute(value, out object normalized))
                {
                    log.SetAttribute(key, normalized);
                }
            }
        }

        const string template = "SimsOnline signal: {0}";
        if (level == SentryLogLevel.Error)
        {
            SentrySdk.Logger.LogError(ConfigureLog, template, signal);
        }
        else if (level == SentryLogLevel.Warning)
        {
            SentrySdk.Logger.LogWarning(ConfigureLog, template, signal);
        }
        else
        {
            SentrySdk.Logger.LogInfo(ConfigureLog, template, signal);
        }
    }

}
