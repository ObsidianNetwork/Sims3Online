using System.Globalization;

namespace SimsOnline.DedicatedServer.Observability;

internal static class SentryEnvironment
{
    public static SimsOnlineTelemetryOptions CreateServerOptions(
        SessionConfig config,
        SentryReleaseInfo release,
        string? dsn)
    {
        return CreateCommonOptions(
            dsn,
            config.SentryEnvironment,
            config.SentryReleaseOverride
                ?? Environment.GetEnvironmentVariable("SENTRY_RELEASE")
                ?? release.Release,
            release.Distribution,
            release.CommitSha,
            "dedicated_server",
            Environment.GetEnvironmentVariable("SIMSONLINE_ROLE") ?? "server",
            config.SaveName) with
        {
            SendDefaultPii = config.SentrySendDefaultPii,
            EnableMetrics = config.SentryEnableMetrics,
            TracesSampleRate = NormalizeSampleRate(config.SentryTracesSampleRate),
            ProfilesSampleRate = NormalizeSampleRate(config.SentryProfilesSampleRate),
            DisableProfiling = config.SentryDisableProfiling,
            Debug = ReadBoolean("SENTRY_DEBUG"),
        };
    }

    public static SimsOnlineTelemetryOptions CreateClientOptions(
        string clientRole,
        string? saveName)
    {
        SentryReleaseInfo release = SentryReleaseResolver.Resolve();
        string environment = Environment.GetEnvironmentVariable("SENTRY_ENVIRONMENT") ?? "development";
        string resolvedRole = Environment.GetEnvironmentVariable("SIMSONLINE_ROLE") ?? clientRole;
        return CreateCommonOptions(
            Environment.GetEnvironmentVariable("SENTRY_DSN"),
            environment,
            Environment.GetEnvironmentVariable("SENTRY_RELEASE") ?? release.Release,
            release.Distribution,
            release.CommitSha,
            "client",
            resolvedRole,
            saveName) with
        {
            SendDefaultPii = ReadBoolean("SENTRY_SEND_DEFAULT_PII"),
            IsGlobalModeEnabled = true,
            EnableMetrics = !ReadBoolean("SENTRY_DISABLE_METRICS"),
            TracesSampleRate = ReadSampleRate("SENTRY_TRACES_SAMPLE_RATE"),
            ProfilesSampleRate = ReadSampleRate("SENTRY_PROFILES_SAMPLE_RATE"),
            DisableProfiling = ReadSampleRate("SENTRY_PROFILES_SAMPLE_RATE") is not > 0.0,
            Debug = ReadBoolean("SENTRY_DEBUG"),
        };
    }

    private static SimsOnlineTelemetryOptions CreateCommonOptions(
        string? dsn,
        string environment,
        string release,
        string distribution,
        string controllerVersion,
        string processRole,
        string clientRole,
        string? saveName)
    {
        return new SimsOnlineTelemetryOptions
        {
            Dsn = dsn,
            Environment = SafeCorrelationValue(environment),
            Release = SafeCorrelationValue(release),
            Distribution = SafeCorrelationValue(distribution),
            ProcessRole = SafeCorrelationValue(processRole),
            ClientRole = SafeCorrelationValue(clientRole),
            ControllerVersion = SafeCorrelationValue(controllerVersion),
            SessionId = SafeCorrelationValue(
                Environment.GetEnvironmentVariable("SIMSONLINE_RUN_ID")
                ?? Guid.NewGuid().ToString("N")),
            InstanceId = Guid.NewGuid().ToString("N"),
            SaveHash = string.IsNullOrWhiteSpace(saveName)
                ? null
                : SentryReleaseResolver.ShortHash(saveName),
            PipelineId = SafeOptionalCorrelationValue(
                Environment.GetEnvironmentVariable("SIMSONLINE_PIPELINE_ID")),
            RunArchivePath = SafeRunArchivePath(
                Environment.GetEnvironmentVariable("SIMSONLINE_RUN_ARCHIVE_PATH")),
            LinearIssue = SafeOptionalCorrelationValue(
                Environment.GetEnvironmentVariable("SIMSONLINE_LINEAR_ISSUE")),
        };
    }

    private static double? ReadSampleRate(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)
            ? NormalizeSampleRate(rate)
            : null;
    }

    private static double? NormalizeSampleRate(double? rate) =>
        rate is >= 0.0 and <= 1.0 ? rate : null;

    private static bool ReadBoolean(string name) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out bool value) && value;

    private static string? SafeRunArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.TrimEnd('/', '\\');
        int separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        string basename = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return SafeOptionalCorrelationValue(basename);
    }

    private static string? SafeOptionalCorrelationValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SafeCorrelationValue(value);

    private static string SafeCorrelationValue(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length is > 0 and <= 128 && trimmed.All(IsSafeCorrelationCharacter))
        {
            return trimmed;
        }

        return $"sha256-{SentryReleaseResolver.ShortHash(trimmed)}";
    }

    private static bool IsSafeCorrelationCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
        || character is '.' or '_' or '-' or ':' or '@' or '+';
}
