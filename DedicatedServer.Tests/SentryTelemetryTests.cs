using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Observability;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    private const string SentryControlMarker = "ulw-sentry-control";
    private const string SentryForbiddenMarker = "ulw-sentry-private";

    [Test("SentryTelemetry.DisabledIsNoOp")]
    private static void SentryTelemetryDisabledIsNoOp()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions(dsn: null);

        using SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(options, transport);
        Assert(!telemetry.IsEnabled, "Telemetry should be disabled when the DSN is absent.");

        telemetry.AddBreadcrumb("controller.starting");
        telemetry.LogInfo("controller.starting");
        telemetry.RecordRuntimeActiveMetric(active: true);
        telemetry.CaptureException(new InvalidOperationException(SentryControlMarker), "stack.incomplete");
        using (SimsOnlineTelemetryTransaction transaction = telemetry.StartTransaction("session.lifecycle", SentryTracesSampler.OpSessionStart))
        {
            transaction.Complete(succeeded: true);
        }

        telemetry.FlushAsync(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
        Assert(transport.Envelopes.Count == 0, "Disabled telemetry must not enqueue envelopes.");
    }

    [Test("SentryTelemetry.OptionsAreConsoleSafe")]
    private static void SentryTelemetryOptionsAreConsoleSafe()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions("https://public@example.invalid/1");
        SentryOptions sdkOptions = new();

        SimsOnlineTelemetry.ApplyOptions(sdkOptions, options, transport);

        Assert(!sdkOptions.IsGlobalModeEnabled, "Concurrent server processes require AsyncLocal scope isolation.");
        SentryOptions clientSdkOptions = new();
        SimsOnlineTelemetry.ApplyOptions(
            clientSdkOptions,
            options with { IsGlobalModeEnabled = true },
            transport);
        Assert(clientSdkOptions.IsGlobalModeEnabled, "The single-user client process requires global scope mode.");
        Assert(sdkOptions.ShutdownTimeout == TimeSpan.FromSeconds(2), "Shutdown flush must be bounded to two seconds.");
        Assert(sdkOptions.EnableLogs, "Native Sentry logs should be enabled.");
        Assert(sdkOptions.EnableMetrics, "Metrics should be enabled when configured.");
        Assert(!sdkOptions.SendDefaultPii, "Default PII collection must remain disabled.");
        Assert(sdkOptions.ServerName == null, "The host machine name must not be configured as server_name.");
        Assert(sdkOptions.TracesSampleRate == 1.0f, "An explicit trace rate must override the operation sampler.");
        Assert(sdkOptions.TracesSampler == null, "Trace rate and sampler are mutually exclusive.");
        Assert(sdkOptions.ProfilesSampleRate == 0.0f, "Profiling must be opt-in rather than 100% by default.");
        Assert(ReferenceEquals(sdkOptions.Transport, transport), "The version-pinned transport seam must be honored.");
    }

    [Test("SentryTelemetry.FailureOnceDeduplicatesByDiagnosticKey")]
    private static void SentryTelemetryFailureOnceDeduplicatesByDiagnosticKey()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions("https://public@example.invalid/1");

        using SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(options, transport);
        SentryId first = telemetry.ReportFailureOnce(
            new InvalidOperationException(SentryControlMarker),
            "mirror.dispatch_failed",
            new Dictionary<string, string?> { ["command_type"] = "ApplySnapshot" });
        SentryId duplicate = telemetry.ReportFailureOnce(
            new InvalidOperationException(SentryControlMarker),
            "mirror.dispatch_failed",
            new Dictionary<string, string?> { ["command_type"] = "ApplySnapshot" });
        SentryId distinct = telemetry.ReportFailureOnce(
            new InvalidOperationException(SentryControlMarker),
            "mirror.dispatch_failed",
            new Dictionary<string, string?> { ["command_type"] = "InteractionQueued" });

        telemetry.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        int eventEnvelopeCount = transport.Envelopes.Count(envelope =>
            envelope.Contains("\"type\":\"event\"", StringComparison.Ordinal));
        Assert(first != SentryId.Empty, "The first high-signal failure must be captured.");
        Assert(duplicate == SentryId.Empty, "An identical diagnostic failure must be suppressed.");
        Assert(distinct != SentryId.Empty, "A distinct command failure must not be suppressed by a shared signal name.");
        Assert(eventEnvelopeCount == 2, $"Expected two distinct error events, got {eventEnvelopeCount}.");
    }

    [Test("SentryTelemetry.LogDropsObjectAttributes")]
    private static void SentryTelemetryLogDropsObjectAttributes()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions("https://public@example.invalid/1");

        using SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(options, transport);
        telemetry.LogWarning("safe.warning", new Dictionary<string, object?>
        {
            ["validation_gate"] = "safe-scalar",
            ["protocol_payload"] = new UnsafeTelemetryValue(),
        });
        telemetry.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        string wire = transport.JoinedWireText();
        Assert(wire.Contains("safe.warning", StringComparison.Ordinal), "The safe warning must reach the wire.");
        Assert(wire.Contains("safe-scalar", StringComparison.Ordinal), "Safe scalar attributes must remain.");
        Assert(!wire.Contains(SentryForbiddenMarker, StringComparison.Ordinal), "Arbitrary object attributes must not reach Sentry logs.");
    }

    [Test("SentryEnvironment.SanitizesCorrelationValues")]
    private static void SentryEnvironmentSanitizesCorrelationValues()
    {
        string? originalRunId = Environment.GetEnvironmentVariable("SIMSONLINE_RUN_ID");
        string? originalPipelineId = Environment.GetEnvironmentVariable("SIMSONLINE_PIPELINE_ID");
        string? originalArchivePath = Environment.GetEnvironmentVariable("SIMSONLINE_RUN_ARCHIVE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("SIMSONLINE_RUN_ID", $"run/{SentryForbiddenMarker}/private");
            Environment.SetEnvironmentVariable("SIMSONLINE_PIPELINE_ID", $"pipeline {SentryForbiddenMarker}");
            Environment.SetEnvironmentVariable("SIMSONLINE_RUN_ARCHIVE_PATH", $"relative/{SentryForbiddenMarker}/archive.json");

            SimsOnlineTelemetryOptions options = SentryEnvironment.CreateClientOptions("client1", "safe.sims3");

            Assert(!options.SessionId.Contains(SentryForbiddenMarker, StringComparison.Ordinal), "Unsafe session ids must be replaced with a stable safe token.");
            Assert(!options.PipelineId!.Contains(SentryForbiddenMarker, StringComparison.Ordinal), "Unsafe pipeline ids must be replaced with a stable safe token.");
            Assert(options.RunArchivePath == "archive.json", $"Only the archive basename may leave the process. Got: {options.RunArchivePath}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMSONLINE_RUN_ID", originalRunId);
            Environment.SetEnvironmentVariable("SIMSONLINE_PIPELINE_ID", originalPipelineId);
            Environment.SetEnvironmentVariable("SIMSONLINE_RUN_ARCHIVE_PATH", originalArchivePath);
        }
    }

    [Test("SentryMirrorMetadata.CapturesInteractionContext")]
    private static void SentryMirrorMetadataCapturesInteractionContext()
    {
        ServerEvent.InteractionQueued queued = new(101, 202, "Terrain.GoHere", 303, null);
        IReadOnlyDictionary<string, string?> queuedAttributes =
            SimsOnlineTelemetryMetadata.ForMirrorEvent(queued, claimedActor: true);
        Assert(queuedAttributes["claimed_sim_id"] == "101", "A claimed actor id must be included.");
        Assert(queuedAttributes["interaction_key"] == "Terrain.GoHere", "The interaction key must be included.");
        Assert(queuedAttributes["target_object_id"] == "303", "The target object id must be included.");

        InteractionReplayBundle replayBundle = new(
            1, 404, 505, "Sims3.Gameplay.Objects.Seating.Sit", null, null, 606, null, null,
            null, null, null, false, InteractionQueueRole.Head, null, null, null,
            new Dictionary<string, string>(), ReplayPolicy.ForceAlign);
        IReadOnlyDictionary<string, string?> replayAttributes =
            SimsOnlineTelemetryMetadata.ForMirrorEvent(
                new ServerEvent.InteractionReplayQueued(replayBundle),
                claimedActor: true);
        Assert(replayAttributes["claimed_sim_id"] == "404", "Replay metadata must include the claimed actor.");
        Assert(replayAttributes["interaction_key"] == replayBundle.InteractionKey, "Replay metadata must include the interaction key.");
        Assert(replayAttributes["target_object_id"] == "606", "Replay metadata must include the target object.");
        Assert(replayAttributes["replay_policy"] == "ForceAlign", "Replay metadata must include the resolved policy.");
    }

    // degrade path emits replay.policy_degraded with interactionKey + policy reason.
    [Test("SentryTelemetry.EmitsReplayPolicyDegradedWithKeyAndReason")]
    private static void SentryTelemetryEmitsReplayPolicyDegradedWithKeyAndReason()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions("https://public@example.invalid/1");

        using SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(options, transport);
        Assert(telemetry.IsEnabled, "Telemetry must be enabled for degrade signal emission.");

        telemetry.EmitReplayPolicyDegraded(
            actorObjectId: 100uL,
            interactionKey: "Sims3.Gameplay.Objects.Decorations.Mirror+GussyUp+Definition",
            policy: "ReplayAsRoute",
            reason: "unknown interaction with target");
        telemetry.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

        string wire = transport.JoinedWireText();
        Assert(wire.Contains("replay.policy_degraded", StringComparison.Ordinal),
            $"Expected replay.policy_degraded on the wire, got: {wire}");
        // JSON envelopes escape '+' as \u002B — match both forms.
        Assert(wire.Contains("Mirror+GussyUp", StringComparison.Ordinal)
                || wire.Contains("Mirror\\u002BGussyUp", StringComparison.Ordinal),
            $"Expected interaction key on the wire, got: {wire}");
        Assert(wire.Contains("ReplayAsRoute", StringComparison.Ordinal),
            $"Expected policy on the wire, got: {wire}");
        Assert(wire.Contains("unknown interaction with target", StringComparison.Ordinal),
            $"Expected policy reason on the wire, got: {wire}");
    }

    [Test("SentryTelemetry.PropagatesTraceAcrossCommand")]
    private static void SentryTelemetryPropagatesTraceAcrossCommand()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions("https://public@example.invalid/1");

        using SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(options, transport);
        ClientCommand.JoinSession outbound = new(Guid.NewGuid(), 7, "safe-player");
        using (SimsOnlineTelemetryTransaction clientTransaction =
            telemetry.StartTransaction("client.command", "client.command"))
        {
            telemetry.AttachTrace(outbound);
            clientTransaction.Complete(succeeded: true);
        }

        Assert(!string.IsNullOrWhiteSpace(outbound.SentryTrace), "The client command must carry a Sentry trace header.");
        ClientCommand decoded = PublicProtocolCodec.DecodeCommand(PublicProtocolCodec.EncodeCommand(outbound));
        Assert(decoded.SentryTrace == outbound.SentryTrace, "The trace header must survive the public protocol codec.");
        Assert(decoded.SentryBaggage == outbound.SentryBaggage, "Dynamic sampling baggage must survive the public protocol codec.");

        using (SimsOnlineTelemetryTransaction serverTransaction = telemetry.StartTransaction(
            "server.command",
            "server.command",
            decoded.SentryTrace,
            decoded.SentryBaggage))
        {
            serverTransaction.Complete(succeeded: true);
        }

        telemetry.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        string traceId = outbound.SentryTrace!.Split('-')[0];
        int traceOccurrences = transport.JoinedWireText().Split(traceId, StringSplitOptions.None).Length - 1;
        Assert(traceOccurrences >= 2, $"Both command-side transactions must share trace {traceId}.");
    }

    [Test("SentryTelemetry.ListenerReportsCaughtCommandFailures")]
    private static void SentryTelemetryListenerReportsCaughtCommandFailures() =>
        RunSentryListenerFailureAsync().GetAwaiter().GetResult();

    private static async Task RunSentryListenerFailureAsync()
    {
        InMemoryAuthoritativeHost host = new() { ThrowOnGetSnapshot = true };
        AuthoritativeSession session = new(host);
        await session.StartAsync("Telemetry.sims3", CancellationToken.None);
        Exception? reportedFailure = null;
        string? reportedCommand = null;
        string? continuedTrace = null;

        await using ClientListenerServer listener = new(
            session,
            "127.0.0.1:0",
            (exception, commandType) =>
            {
                reportedFailure = exception;
                reportedCommand = commandType;
            },
            command =>
            {
                continuedTrace = command.SentryTrace;
                return SimsOnlineTelemetryTransaction.Disabled;
            });
        await listener.StartAsync(CancellationToken.None);

        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Loopback, listener.BoundEndpoint.Port, timeout.Token);
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        ClientCommand.JoinSession command = new(Guid.NewGuid(), 9, "safe-player")
        {
            SentryTrace = "0123456789abcdef0123456789abcdef-0123456789abcdef-1",
        };
        byte[] payload = Encoding.UTF8.GetBytes(PublicProtocolCodec.EncodeCommand(command) + "\n");
        await stream.WriteAsync(payload, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        _ = await reader.ReadLineAsync(timeout.Token);

        Assert(reportedFailure is InvalidOperationException, "Caught authoritative command exceptions must reach the telemetry reporter.");
        Assert(reportedCommand == nameof(ClientCommand.JoinSession), $"Expected JoinSession context. Got: {reportedCommand}");
        Assert(continuedTrace == command.SentryTrace, "The listener transaction factory must receive the propagated trace header.");
    }

    [Test("SentryEnvironment.DebugIsExplicitOptIn")]
    private static void SentryEnvironmentDebugIsExplicitOptIn()
    {
        string? original = Environment.GetEnvironmentVariable("SENTRY_DEBUG");
        try
        {
            SessionConfig config = SessionConfig.FromArgs(new[] { "--sentry-environment", "development" });
            SentryReleaseInfo release = SentryReleaseResolver.Parse("abc1234", "");

            Environment.SetEnvironmentVariable("SENTRY_DEBUG", null);
            SimsOnlineTelemetryOptions quiet = SentryEnvironment.CreateServerOptions(config, release, "https://public@example.invalid/1");
            Assert(!quiet.Debug, "Development telemetry must not expose SDK diagnostics or the DSN unless explicitly requested.");

            Environment.SetEnvironmentVariable("SENTRY_DEBUG", "true");
            SimsOnlineTelemetryOptions diagnostic = SentryEnvironment.CreateServerOptions(config, release, "https://public@example.invalid/1");
            Assert(diagnostic.Debug, "SENTRY_DEBUG=true should explicitly enable SDK diagnostics.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SENTRY_DEBUG", original);
        }
    }

    [Test("SentryTelemetry.EmitsCorrelatedPrivacySafeSignals")]
    private static void SentryTelemetryEmitsCorrelatedPrivacySafeSignals()
    {
        RecordingTransport transport = new();
        SimsOnlineTelemetryOptions options = CreateTelemetryOptions("https://public@example.invalid/1");

        using SimsOnlineTelemetry telemetry = SimsOnlineTelemetry.Start(options, transport);
        Assert(telemetry.IsEnabled, "Telemetry should be enabled for a non-empty DSN.");
        telemetry.SetTs3ProcessId(4242);

        telemetry.AddBreadcrumb("controller.starting", new Dictionary<string, string?>
        {
            ["validation_gate"] = "dual-endpoint",
            ["sim_name"] = SentryForbiddenMarker,
        });

        using (SimsOnlineTelemetryTransaction transaction = telemetry.StartTransaction("session.lifecycle", SentryTracesSampler.OpSessionStart))
        {
            using SimsOnlineTelemetrySpan span = transaction.StartChild("bridge.connect", "connect host bridge");
            span.Complete(succeeded: true);
            telemetry.LogInfo("controller.starting", new Dictionary<string, object?>
            {
                ["validation_gate"] = "dual-endpoint",
                ["mirror.payload.position"] = SentryForbiddenMarker,
            });
            telemetry.RecordRuntimeActiveMetric(active: true);
            telemetry.CaptureException(
                new FileNotFoundException($"{SentryForbiddenMarker} raw bridge payload at /home/{SentryForbiddenMarker}/save.sims3"),
                "stack.incomplete",
                new Dictionary<string, string?>
                {
                    ["control_marker"] = SentryControlMarker,
                    ["validation_gate"] = "dual-endpoint",
                    ["household_name"] = SentryForbiddenMarker,
                });
            transaction.Complete(succeeded: false);
        }

        telemetry.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

        string wire = transport.JoinedWireText();
        Assert(transport.Envelopes.Count >= 3, $"Expected multiple signal envelopes, got {transport.Envelopes.Count}. Wire: {wire}");
        Assert(wire.Contains("\"type\":\"event\"", StringComparison.Ordinal), "An error event envelope must be emitted.");
        Assert(wire.Contains("\"type\":\"transaction\"", StringComparison.Ordinal), "A transaction envelope must be emitted.");
        Assert(wire.Contains("stack.incomplete", StringComparison.Ordinal), "The high-signal event name must reach the wire.");
        Assert(wire.Contains("simsonline.runtime.active", StringComparison.Ordinal), "The runtime activity metric must reach the wire.");
        Assert(wire.Contains(SentryControlMarker, StringComparison.Ordinal), "The benign control marker proves the event was not vacuously dropped.");
        Assert(wire.Contains("run-ulw-sentry", StringComparison.Ordinal), "session_id/run correlation must reach the wire.");
        Assert(wire.Contains("client1", StringComparison.Ordinal), "client_role must reach the wire.");
        Assert(wire.Contains("ts3_pid", StringComparison.Ordinal) && wire.Contains("4242", StringComparison.Ordinal), "The live TS3 pid must reach every signal family.");
        Assert(wire.Contains("controller_version", StringComparison.Ordinal), "The legacy controller version tag must remain queryable.");
        Assert(wire.Contains("dual-endpoint", StringComparison.Ordinal), "Safe validation metadata must reach the wire.");
        string logEnvelope = transport.Envelopes.First(envelope =>
            envelope.Contains("\"type\":\"log\"", StringComparison.Ordinal));
        string metricEnvelope = transport.Envelopes.First(envelope =>
            envelope.Contains("\"type\":\"trace_metric\"", StringComparison.Ordinal));
        Assert(logEnvelope.Contains("save_hash", StringComparison.Ordinal), "Native lifecycle logs must carry save_hash correlation.");
        Assert(metricEnvelope.Contains("save_hash", StringComparison.Ordinal), "Runtime activity metrics must carry save_hash correlation.");
        Assert(!wire.Contains("bridge_port", StringComparison.Ordinal), "The host bridge is memory:<process>, so no signal carries a bridge_port.");
        Assert(!wire.Contains(SentryForbiddenMarker, StringComparison.Ordinal), $"Forbidden PII/payload values must not reach the wire. Wire: {wire}");
        Assert(!wire.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "The machine hostname must not reach the wire.");
        Assert(!wire.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal), "The local user-profile path must not reach the wire.");
    }

    [Test("SentryPrivacyFilter.EventStripsPii")]
    private static void SentryPrivacyFilterEventStripsPii()
    {
        SentryEvent sentryEvent = new()
        {
            ServerName = SentryForbiddenMarker,
            User = new SentryUser { Username = SentryForbiddenMarker },
        };
        sentryEvent.SetTag("sim_name", SentryForbiddenMarker);
        sentryEvent.SetTag("validation_gate", "dual-endpoint");

        SentryEvent? filtered = SentryPrivacyFilter.StripEventPii(sentryEvent);

        Assert(filtered != null, "A normal error event should remain sendable.");
        Assert(filtered.ServerName == null, "server_name must be removed from error events.");
        AssertUserIsEmpty(filtered.User, "error event");
        Assert(!filtered.Tags.ContainsKey("sim_name"), "Sensitive error tags must be removed.");
        Assert(filtered.Tags.TryGetValue("validation_gate", out string? gate) && gate == "dual-endpoint", "Safe error tags must remain.");
    }

    [Test("SentryPrivacyFilter.TransactionStripsPii")]
    private static void SentryPrivacyFilterTransactionStripsPii()
    {
        SentryTransaction transaction = new("session.lifecycle", SentryTracesSampler.OpSessionStart)
        {
            User = new SentryUser { Username = SentryForbiddenMarker },
        };
        transaction.SetTag("bridge.command_payload.actor", SentryForbiddenMarker);
        transaction.SetTag("validation_gate", "dual-endpoint");

        SentryTransaction filtered = SentryPrivacyFilter.StripTransactionPii(transaction);

        AssertUserIsEmpty(filtered.User, "transaction");
        Assert(!filtered.Tags.ContainsKey("bridge.command_payload.actor"), "Sensitive transaction tags must be removed.");
        Assert(filtered.Tags.TryGetValue("validation_gate", out string? gate) && gate == "dual-endpoint", "Safe transaction tags must remain.");
    }

    [Test("SentryPrivacyFilter.DropsCancellation")]
    private static void SentryPrivacyFilterDropsCancellation()
    {
        SentryEvent canceled = new(new OperationCanceledException(SentryForbiddenMarker));
        Assert(SentryPrivacyFilter.StripEventPii(canceled) == null, "Expected cancellation must not create an error issue.");
    }

    private static SimsOnlineTelemetryOptions CreateTelemetryOptions(string? dsn) => new()
    {
        Dsn = dsn,
        Environment = "development",
        Release = "simsonline@ulw-test",
        Distribution = "1",
        ProcessRole = "client",
        ClientRole = "client1",
        ControllerVersion = "ulw-test",
        SessionId = "run-ulw-sentry",
        InstanceId = "instance-ulw-sentry",
        SaveHash = "0123456789ab",
        PipelineId = "PILOT-1",
        RunArchivePath = ".omo/run/archive-ulw",
        LinearIssue = "PILOT-1",
        SendDefaultPii = false,
        EnableMetrics = true,
        TracesSampleRate = 1.0,
        ProfilesSampleRate = 0.0,
        DisableProfiling = true,
        Debug = false,
    };

    private sealed class UnsafeTelemetryValue
    {
        public override string ToString() => SentryForbiddenMarker;
    }

    private static void AssertUserIsEmpty(SentryUser user, string signalType)
    {
        Assert(user.Id == null, $"User id must be removed from the {signalType}.");
        Assert(user.Username == null, $"Username must be removed from the {signalType}.");
        Assert(user.Email == null, $"User email must be removed from the {signalType}.");
        Assert(user.IpAddress == null, $"User IP must be removed from the {signalType}.");
        Assert(user.Other.Count == 0, $"Custom user fields must be removed from the {signalType}.");
    }

    private sealed class RecordingTransport : ITransport
    {
        public ConcurrentQueue<string> Envelopes { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            await using MemoryStream stream = new();
            await envelope.SerializeAsync(stream, SilentDiagnosticLogger.Instance, cancellationToken);
            Envelopes.Enqueue(Encoding.UTF8.GetString(stream.ToArray()));
        }

        public string JoinedWireText() => string.Join("\n", Envelopes);
    }

    private sealed class SilentDiagnosticLogger : IDiagnosticLogger
    {
        public static SilentDiagnosticLogger Instance { get; } = new();

        public bool IsEnabled(SentryLevel level) => false;

        public void Log(SentryLevel logLevel, string message, Exception? exception = null, params object?[] args)
        {
        }
    }
}
