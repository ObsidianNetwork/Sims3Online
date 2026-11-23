using SimsOnline.DedicatedServer.HostBridge;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// W2 (internal documentation): the client authority gate state a client TS3 should run
/// (forked <c>AuthorityHook</c>, bridge verb <c>SET_AUTHORITY</c>). The gate lets a Sim queue
/// interactions locally only if the client runs it natively; everything else must come from the
/// server. Which owned Sims run natively depends on the lot-local authority mode: in
/// FullPuppetFallback the owner renders the server too, so no Sim is native; in
/// SoloNative / ProxMirror the client's claimed Sims are. Owned ids are sorted so equal states
/// compare equal regardless of claim order.
/// </summary>
public sealed record ClientAuthorityState(AuthorityGateMode Mode, IReadOnlyList<ulong> NativeSimObjectIds)
{
    public bool SameAs(ClientAuthorityState? other) =>
        other is not null
        && Mode == other.Mode
        && NativeSimObjectIds.SequenceEqual(other.NativeSimObjectIds);

    public override string ToString() =>
        $"mode={Mode.ToString().ToLowerInvariant()} native=[{string.Join(",", NativeSimObjectIds)}]";
}

public static class ClientAuthorityPolicy
{
    /// <summary>Environment variable that sets the configured gate mode (off | observe | enforce).</summary>
    public const string ModeEnvironmentVariable = "SIMSONLINE_AUTHORITY_GATE";

    /// <summary>Enforce by default: proven on a real two-TS3 run (cold-test-021). On stock gameplay the
    /// hook is absent and the mode has no effect. Observe stays available for diagnosis.</summary>
    public const AuthorityGateMode DefaultMode = AuthorityGateMode.Enforce;

    /// <summary>
    /// W2 step 3, REVERSED by the Card 45 E4 forensics (2026-10-30): the gate never replaces
    /// mirror autonomy suppression. The live probes caught the replacement red-handed —
    /// with the gate enforcing (`hook=present`), only the Sim that received the claim-time
    /// <c>SimAutonomyChanged</c> was suppressed on the client (disabledCount=1) while every other
    /// mirrored Sim sat at <c>enabled=true</c> and ran engine-native autonomy (the puppet's
    /// <c>WashDishes</c>/props/moodlets are the blocking ledger rows). The forked AuthorityHook
    /// gates SCRIPT-level queue inserts; it does not gate the local autonomous director, so the
    /// premise "a mirrored Sim can then only queue what the server sends" is false. Suppression
    /// stays on; APPLY_AUTONOMY is an idempotent absolute-set, so overlap with the gate is inert.
    /// Kept as a single decision point so the caller and tests stay honest about WHY it is constant.
    /// </summary>
    public static bool GateReplacesAutonomySuppression(AuthorityGateMode mode, string? hostReport) => false;

    /// <summary>A rejected state is not resent before this (an older mod without SET_AUTHORITY).</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    public static bool TryParseMode(string? text, out AuthorityGateMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off":
                mode = AuthorityGateMode.Off;
                return true;
            case "observe":
                mode = AuthorityGateMode.Observe;
                return true;
            case "enforce":
                mode = AuthorityGateMode.Enforce;
                return true;
            default:
                mode = DefaultMode;
                return false;
        }
    }

    public static ClientAuthorityState Compute(
        AuthorityGateMode configuredMode,
        LotLocalAuthorityMode lotMode,
        IReadOnlyCollection<ulong> ownedSimObjectIds)
    {
        ArgumentNullException.ThrowIfNull(ownedSimObjectIds);
        List<ulong> native = LotLocalAuthorityMirrorGate.OwnsNativeSimulation(lotMode)
            ? ownedSimObjectIds.ToList()
            : new List<ulong>();
        native.Sort();
        return new ClientAuthorityState(configuredMode, native);
    }

    /// <summary>Same rule as <see cref="SimulationScopePolicy.ShouldPublish"/>: send on change, and
    /// hold a just-failed state back until <paramref name="retryAfter"/> has passed.</summary>
    public static bool ShouldPublish(
        ClientAuthorityState next,
        ClientAuthorityState? published,
        ClientAuthorityState? failed,
        DateTimeOffset failedUtc,
        DateTimeOffset now,
        TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.SameAs(published))
        {
            return false;
        }

        return !(next.SameAs(failed) && now - failedUtc < retryAfter);
    }
}
