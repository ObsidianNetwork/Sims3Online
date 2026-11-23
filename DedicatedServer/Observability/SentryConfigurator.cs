namespace SimsOnline.DedicatedServer.Observability;

public static class SentryConfigurator
{
    internal static SimsOnlineTelemetry Configure(
        SessionConfig config,
        SentryReleaseInfo release)
    {
        string? dsn = DetermineDsn(config.SentryDsnEnv);
        SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(
            SentryEnvironment.CreateServerOptions(config, release, dsn));
        ReportDisabled(telemetry);
        return telemetry;
    }

    public static SimsOnlineTelemetry ConfigureClient(
        string clientRole,
        string? saveName)
    {
        SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(
            SentryEnvironment.CreateClientOptions(clientRole, saveName));
        ReportDisabled(telemetry);
        return telemetry;
    }

    private static string? DetermineDsn(string? configuredEnvironmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(configuredEnvironmentVariable))
        {
            return Environment.GetEnvironmentVariable(configuredEnvironmentVariable);
        }

        return Environment.GetEnvironmentVariable("SENTRY_DSN");
    }

    private static void ReportDisabled(SimsOnlineTelemetry telemetry)
    {
        if (!telemetry.IsEnabled)
        {
            Console.Error.WriteLine("SENTRY_DSN not set; Sentry SDK disabled.");
        }
    }
}
