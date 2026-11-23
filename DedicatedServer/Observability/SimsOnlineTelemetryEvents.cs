using global::Sentry;

namespace SimsOnline.DedicatedServer.Observability;

/// <summary>
/// named failure-class events. Each method routes through the SimsOnlineTelemetry
/// wrapper (never raw SentrySdk) and carries the shared run identity + required tags.
/// Callers pass only the event-specific attributes;BaseObjectAttributes supplies the rest.
/// </summary>
public sealed partial class SimsOnlineTelemetry
{
    // named failure-class signals. Keep alphabetized for grep auditability.
    // Every emission MUST go through EmitFailureEvent so the privacy filter + dedupe gate apply.

    /// <summary>Bridge CONNECT was written but HELLO was never received within timeout.</summary>
    public void EmitBridgeHelloMissing(string? bridgeEndpoint = null, int? protocolVersion = null)
    {
        EmitFailureEvent("bridge.hello_missing", new Dictionary<string, string?>
        {
            ["bridge_endpoint"] = bridgeEndpoint,
            ["protocol_version"] = protocolVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>Engine speed diverges from commanded speed (speed=None or silent pause).</summary>
    public void EmitBridgeSpeedStateInvalid(string commandedSpeed, string observedSpeed)
    {
        EmitFailureEvent("bridge.speed_state_invalid", new Dictionary<string, string?>
        {
            ["commanded_speed"] = commandedSpeed,
            ["observed_speed"] = observedSpeed,
        });
    }

    /// <summary>Snapshot cadence drift exceeds the configured threshold.</summary>
    public void EmitSnapshotCadenceDrift(long serverTick, long clientAckTick, long lagTicks)
    {
        EmitFailureEvent("snapshot.cadence_drift", new Dictionary<string, string?>
        {
            ["server_tick"] = serverTick.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["client_ack_tick"] = clientAckTick.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["lag_ticks"] = lagTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>Mirror dispatch failed for a supported interaction key (partial_failure).</summary>
    public void EmitReplayDispatchPartialFailure(ulong actorObjectId, string interactionKey, string reason)
    {
        EmitFailureEvent("replay.dispatch_partial_failure", new Dictionary<string, string?>
        {
            ["actor_object_id"] = actorObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["interaction_key"] = interactionKey,
            ["reason"] = reason,
        });
    }

    /// <summary>
    /// mirror-apply degraded an unsupported/unknown family to route or slide
    /// (or filtered NeverReplay). Diagnostic signal — not an unhandled error.
    /// </summary>
    public void EmitReplayPolicyDegraded(
        ulong actorObjectId,
        string interactionKey,
        string policy,
        string reason)
    {
        if (!IsEnabled)
        {
            return;
        }

        Dictionary<string, string?> attributes = new()
        {
            ["actor_object_id"] = actorObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["interaction_key"] = interactionKey,
            ["replay_policy"] = policy,
            ["reason"] = reason,
        };

        SentrySdk.AddBreadcrumb(
            "replay.policy_degraded",
            "simsonline.replay",
            "state",
            SafeStringAttributes(attributes),
            BreadcrumbLevel.Info);

        LogInfo("replay.policy_degraded", ToObjectAttributes(attributes));
    }

    /// <summary>Mirror target Sim resolution failed (Sim not found on client).</summary>
    public void EmitReplayTargetResolutionFailed(ulong targetObjectId, string? interactionKey = null)
    {
        EmitFailureEvent("replay.target_resolution_failed", new Dictionary<string, string?>
        {
            ["target_object_id"] = targetObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["interaction_key"] = interactionKey,
        });
    }

    /// <summary>
    /// Central emission path for named failure-class events. Applies the privacy
    /// filter + dedupe (ReportFailureOnce semantics) so each unique signal+attributes
    /// combination fires at most once per session. Routes through SentrySdk.AddBreadcrumb
    /// (not CaptureException) because these are diagnostic signals, not unhandled errors.
    /// </summary>
    private void EmitFailureEvent(
        string signal,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Dedupe: each unique signal fires at most once per session.
        if (!_reportedFailureSignals.TryAdd(signal, 0))
        {
            return;
        }

        SentrySdk.AddBreadcrumb(
            signal,
            "simsonline.failure",
            "state",
            SafeStringAttributes(attributes),
            BreadcrumbLevel.Warning);

        // Also emit as a log so the local-JSON fanout sink captures it even when Sentry is not configured.
        LogWarning(signal, ToObjectAttributes(attributes));
    }
}
