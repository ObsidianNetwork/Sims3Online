using global::Sentry;
using global::Sentry.Extensibility;
using global::Sentry.Profiling;

namespace SimsOnline.DedicatedServer.Observability;

public sealed partial class SimsOnlineTelemetry
{
    internal static void ApplyOptions(
        SentryOptions sentryOptions,
        SimsOnlineTelemetryOptions options,
        ITransport? transport = null)
    {
        sentryOptions.Dsn = options.Dsn;
        sentryOptions.Release = options.Release;
        sentryOptions.Distribution = options.Distribution;
        sentryOptions.Environment = options.Environment;
        sentryOptions.SendDefaultPii = options.SendDefaultPii;
        sentryOptions.ServerName = null;
        sentryOptions.IsGlobalModeEnabled = options.IsGlobalModeEnabled;
        sentryOptions.ShutdownTimeout = DefaultFlushTimeout;
        sentryOptions.MaxBreadcrumbs = 100;
        sentryOptions.StackTraceMode = StackTraceMode.Enhanced;
        sentryOptions.AttachStacktrace = true;
        sentryOptions.AutoSessionTracking = true;
        sentryOptions.EnableLogs = true;
        sentryOptions.EnableMetrics = options.EnableMetrics;
        sentryOptions.Debug = options.Debug;
        sentryOptions.SampleRate = 1.0f;

        if (options.TracesSampleRate.HasValue)
        {
            sentryOptions.TracesSampleRate = (float)options.TracesSampleRate.Value;
            sentryOptions.TracesSampler = null;
        }
        else
        {
            sentryOptions.TracesSampler = SentryTracesSampler.Sample;
        }

        float profilesSampleRate = (float)(options.ProfilesSampleRate ?? 0.0);
        sentryOptions.ProfilesSampleRate = profilesSampleRate;
        if (profilesSampleRate > 0.0f && !options.DisableProfiling)
        {
            sentryOptions.AddIntegration(new ProfilingIntegration(TimeSpan.FromMilliseconds(500)));
        }

        sentryOptions.SetBeforeSend(sentryEvent =>
            SentryPrivacyFilter.StripEventPii(sentryEvent, options.SendDefaultPii));
        sentryOptions.SetBeforeSendTransaction(transaction =>
            SentryPrivacyFilter.StripTransactionPii(transaction, options.SendDefaultPii));
        sentryOptions.SetBeforeBreadcrumb(SentryPrivacyFilter.StripBreadcrumbPii);
        sentryOptions.SetBeforeSendLog(SentryPrivacyFilter.StripLogPii);
        sentryOptions.SetBeforeSendMetric(SentryPrivacyFilter.StripMetricPii);

        if (transport != null)
        {
            sentryOptions.Transport = transport;
        }
    }
}
