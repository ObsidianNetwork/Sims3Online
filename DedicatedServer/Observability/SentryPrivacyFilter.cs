using global::Sentry;
using System.Text.RegularExpressions;

namespace SimsOnline.DedicatedServer.Observability;

internal static class SentryPrivacyFilter
{
    private const string FilteredValue = "[Filtered]";

    private static readonly Regex UnixUserPath = new(
        @"/(?:home|Users)/[^/\s\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex WindowsUserPath = new(
        @"[a-z]:\\Users\\[^\\\s\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StrippedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sim_name",
        "household_name",
        "save_name",
        "server_name",
        "server.address",
        "user.id",
        "user.name",
        "user.email",
        "user.ip",
    };

    private static readonly string[] StrippedPrefixes =
    {
        "bridge.command_payload.",
        "mirror.payload.",
    };

    public static SentryEvent? StripEventPii(SentryEvent sentryEvent) =>
        StripEventPii(sentryEvent, sendDefaultPii: false);

    public static SentryEvent? StripEventPii(SentryEvent sentryEvent, bool sendDefaultPii)
    {
        if (sentryEvent.Exception is OperationCanceledException)
        {
            return null;
        }

        sentryEvent.ServerName = null;
        if (!sendDefaultPii)
        {
            sentryEvent.User = new SentryUser();
        }

        foreach ((string key, string value) in sentryEvent.Tags.ToArray())
        {
            if (ShouldStripTag(key))
            {
                sentryEvent.UnsetTag(key);
            }
            else
            {
                sentryEvent.SetTag(key, SanitizeText(value)!);
            }
        }

        foreach ((string key, object? value) in sentryEvent.Extra.ToArray())
        {
            if (ShouldStripTag(key))
            {
                sentryEvent.SetExtra(key, FilteredValue);
            }
            else if (value is string text)
            {
                sentryEvent.SetExtra(key, SanitizeText(text)!);
            }
        }

        foreach (global::Sentry.Protocol.SentryException exception in
            sentryEvent.SentryExceptions ?? Enumerable.Empty<global::Sentry.Protocol.SentryException>())
        {
            exception.Value = "SimsOnline operation failed.";
            SanitizeFrames(exception.Stacktrace);
        }

        foreach (SentryThread thread in sentryEvent.SentryThreads ?? Enumerable.Empty<SentryThread>())
        {
            SanitizeFrames(thread.Stacktrace);
        }

        foreach (global::Sentry.Protocol.DebugImage image in
            sentryEvent.DebugImages ?? Enumerable.Empty<global::Sentry.Protocol.DebugImage>())
        {
            image.DebugFile = SanitizeText(image.DebugFile);
            image.CodeFile = SanitizeText(image.CodeFile);
        }

        if (sentryEvent.Message != null)
        {
            sentryEvent.Message.Message = SanitizeText(sentryEvent.Message.Message)!;
            sentryEvent.Message.Formatted = SanitizeText(sentryEvent.Message.Formatted);
            sentryEvent.Message.Params = sentryEvent.Message.Params?
                .Select(value => value is string text ? SanitizeText(text)! : value)
                .ToArray();
        }

        return sentryEvent;
    }

    public static SentryTransaction StripTransactionPii(SentryTransaction transaction) =>
        StripTransactionPii(transaction, sendDefaultPii: false);

    public static SentryTransaction StripTransactionPii(
        SentryTransaction transaction,
        bool sendDefaultPii)
    {
        if (!sendDefaultPii)
        {
            transaction.User = new SentryUser();
        }

        foreach ((string key, string value) in transaction.Tags.ToArray())
        {
            if (ShouldStripTag(key))
            {
                transaction.UnsetTag(key);
            }
            else
            {
                transaction.SetTag(key, SanitizeText(value)!);
            }
        }

        return transaction;
    }

    public static SentryLog? StripLogPii(SentryLog log)
    {
        foreach (string key in StrippedKeys)
        {
            if (log.TryGetAttribute(key, out _))
            {
                return null;
            }
        }

        log.SetAttribute("server.address", FilteredValue);
        return log;
    }

    public static SentryMetric? StripMetricPii(SentryMetric metric)
    {
        foreach (string key in StrippedKeys)
        {
            if (metric.TryGetAttribute<object>(key, out _))
            {
                return null;
            }
        }

        metric.SetAttribute("server.address", FilteredValue);
        return metric;
    }

    public static Breadcrumb StripBreadcrumbPii(Breadcrumb breadcrumb)
    {
        Dictionary<string, string> data = new(StringComparer.Ordinal);
        foreach ((string key, string value) in
            breadcrumb.Data ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!ShouldStripTag(key))
            {
                data[key] = SanitizeText(value)!;
            }
        }

        string message = string.Equals(breadcrumb.Category, "Exception", StringComparison.OrdinalIgnoreCase)
            ? "SimsOnline captured exception."
            : SanitizeText(breadcrumb.Message)!;
        return new Breadcrumb(
            message,
            breadcrumb.Type ?? "default",
            data,
            breadcrumb.Category,
            breadcrumb.Level);
    }

    internal static bool ShouldStripTag(string key)
    {
        if (StrippedKeys.Contains(key))
        {
            return true;
        }

        return StrippedPrefixes.Any(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? SanitizeText(string? value)
    {
        if (value == null)
        {
            return null;
        }

        string sanitized = UnixUserPath.Replace(value, "/home/[Filtered]");
        return WindowsUserPath.Replace(sanitized, @"C:\Users\[Filtered]");
    }

    private static void SanitizeFrames(SentryStackTrace? stackTrace)
    {
        if (stackTrace == null)
        {
            return;
        }

        foreach (SentryStackFrame frame in stackTrace.Frames)
        {
            frame.AbsolutePath = SanitizeText(frame.AbsolutePath);
            frame.FileName = SanitizeText(frame.FileName);
            frame.Module = SanitizeText(frame.Module);
        }
    }
}
