namespace SimsOnline.DedicatedServer.Observability;

internal sealed record SimsOnlineTelemetryOptions
{
    public string? Dsn { get; init; }

    public string Environment { get; init; } = "development";

    public string Release { get; init; } = "simsonline-dedicatedserver@unknown";

    public string Distribution { get; init; } = "simsonline";

    public string ProcessRole { get; init; } = "unknown";

    public string ClientRole { get; init; } = "unknown";

    public string ControllerVersion { get; init; } = "unknown";

    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    public string? SaveHash { get; init; }

    public string? PipelineId { get; init; }

    public string? RunArchivePath { get; init; }

    public string? LinearIssue { get; init; }

    public bool SendDefaultPii { get; init; }

    public bool IsGlobalModeEnabled { get; init; }

    public bool EnableMetrics { get; init; } = true;

    public double? TracesSampleRate { get; init; }

    public double? ProfilesSampleRate { get; init; }

    public bool DisableProfiling { get; init; }

    public bool Debug { get; init; }
}
