using global::Sentry;

namespace SimsOnline.DedicatedServer.Observability;

public sealed partial class SimsOnlineTelemetry
{
    private static void ApplyScope(SimsOnlineTelemetryOptions options)
    {
        SentrySdk.ConfigureScope(scope =>
        {
            scope.SetTag("process_role", options.ProcessRole);
            scope.SetTag("client_role", options.ClientRole);
            scope.SetTag("controller_role", options.ProcessRole);
            scope.SetTag("controller_version", options.ControllerVersion);
            scope.SetTag("session_id", options.SessionId);
            scope.SetTag("instance_id", options.InstanceId);
            SetTagIfPresent(scope, "save_hash", options.SaveHash);
            SetTagIfPresent(scope, "pipeline_id", options.PipelineId);
            SetTagIfPresent(scope, "linear_issue", options.LinearIssue);
            if (!string.IsNullOrWhiteSpace(options.RunArchivePath))
            {
                scope.SetExtra("run_archive_path", options.RunArchivePath);
            }

            if (!options.SendDefaultPii)
            {
                scope.User = new SentryUser();
            }
        });
    }

    private IEnumerable<KeyValuePair<string, object>> BaseObjectAttributes(string signal)
    {
        yield return new KeyValuePair<string, object>("signal", signal);
        yield return new KeyValuePair<string, object>("process_role", _options.ProcessRole);
        yield return new KeyValuePair<string, object>("client_role", _options.ClientRole);
        yield return new KeyValuePair<string, object>("controller_role", _options.ProcessRole);
        yield return new KeyValuePair<string, object>("controller_version", _options.ControllerVersion);
        yield return new KeyValuePair<string, object>("session_id", _options.SessionId);
        yield return new KeyValuePair<string, object>("instance_id", _options.InstanceId);
        if (!string.IsNullOrWhiteSpace(_options.SaveHash))
        {
            yield return new KeyValuePair<string, object>("save_hash", _options.SaveHash);
        }

        int ts3ProcessId = Volatile.Read(ref _ts3ProcessId);
        if (ts3ProcessId > 0)
        {
            yield return new KeyValuePair<string, object>("ts3_pid", ts3ProcessId);
        }

        if (!string.IsNullOrWhiteSpace(_options.PipelineId))
        {
            yield return new KeyValuePair<string, object>("pipeline_id", _options.PipelineId);
        }

        if (!string.IsNullOrWhiteSpace(_options.LinearIssue))
        {
            yield return new KeyValuePair<string, object>("linear_issue", _options.LinearIssue);
        }

        if (!string.IsNullOrWhiteSpace(_options.RunArchivePath))
        {
            yield return new KeyValuePair<string, object>("run_archive_path", _options.RunArchivePath);
        }
    }

    private static Dictionary<string, string> SafeStringAttributes(
        IReadOnlyDictionary<string, string?>? attributes)
    {
        Dictionary<string, string> safe = new(StringComparer.Ordinal);
        if (attributes == null)
        {
            return safe;
        }

        foreach ((string key, string? value) in attributes)
        {
            if (value != null && !SentryPrivacyFilter.ShouldStripTag(key))
            {
                safe[key] = SentryPrivacyFilter.SanitizeText(value)!;
            }
        }

        return safe;
    }

    private static void ApplyScopeAttributes(
        Scope scope,
        IReadOnlyDictionary<string, string?>? attributes)
    {
        if (attributes == null)
        {
            return;
        }

        foreach ((string key, string? value) in attributes)
        {
            if (value != null && !SentryPrivacyFilter.ShouldStripTag(key))
            {
                scope.SetTag(key, SentryPrivacyFilter.SanitizeText(value)!);
            }
        }
    }

    private static IReadOnlyDictionary<string, object?>? ToObjectAttributes(
        IReadOnlyDictionary<string, string?>? attributes) =>
        attributes?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);

    private static void SetTagIfPresent(Scope scope, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            scope.SetTag(key, value);
        }
    }

    private static string BuildFailureKey(
        Exception exception,
        string signal,
        IReadOnlyDictionary<string, string?>? attributes)
    {
        System.Text.StringBuilder builder = new(signal.Length + 64);
        builder.Append(signal).Append('|').Append(exception.GetType().FullName);
        if (attributes != null)
        {
            foreach ((string key, string? value) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (value != null && !SentryPrivacyFilter.ShouldStripTag(key))
                {
                    builder.Append('|').Append(key).Append('=').Append(SentryPrivacyFilter.SanitizeText(value));
                }
            }
        }

        return builder.ToString();
    }

    private static bool TryNormalizeLogAttribute(object value, out object normalized)
    {
        switch (value)
        {
            case string text:
                normalized = SentryPrivacyFilter.SanitizeText(text)!;
                return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                normalized = value;
                return true;
            case char character:
                normalized = character.ToString();
                return true;
            case Enum enumValue:
                normalized = enumValue.ToString();
                return true;
            case Guid guid:
                normalized = guid.ToString("D");
                return true;
            default:
                normalized = string.Empty;
                return false;
        }
    }
}
