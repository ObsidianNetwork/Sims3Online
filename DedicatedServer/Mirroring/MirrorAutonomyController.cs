using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// the interaction-decision classifier for a mirrored Sim. Given the server's
/// authoritative <see cref="SimSnapshot.CurrentInteractionKey"/> (a fully-qualified interaction
/// definition type name, e.g. <c>Sims3.Gameplay.Core.Terrain+GoHere</c>, or null/empty when idle),
/// decide which replay class the interaction belongs to. Only <see cref="GoHere"/> is replayable
/// today (the mod's <c>APPLY_INTERACTION_QUEUE</c> handler accepts only <c>Terrain.GoHere</c>).
/// </summary>
public enum MirrorReplayClass
{
    /// <summary>The Sim is idle, or doing something the client cannot re-queue locally — no replay.</summary>
    None = 0,

    /// <summary>A <c>Terrain.GoHere</c>-class interaction — the one interaction the client can re-queue.</summary>
    GoHere = 1,
}

/// <summary>
/// client-mirror autonomy controller — the PURE decision core for the unclaimed/remote-owned
/// "interaction-decision mirroring" model, modelled on the <c>AutonomyCommandGate</c>: this
/// class owns the WHEN (which Sims to suppress, and the first-sight dedupe), the caller (the mirror
/// engine / client) owns the side effects (dispatching the emitted <see cref="BridgeCommand"/>s over
/// the bridge). No engine types, no I/O — unit-testable in <c>DedicatedServer.Tests</c>.
///
/// Policy:
///
///   * A Sim the client is MIRRORING (NOT in the owning client's <c>s_ownedSims</c>) must NOT run
///     its own local autonomy. The living world runs on the HOST authority, including townie free
///     will; the client is a faithful state mirror, so local autonomy on a mirrored Sim
///     only fights the authoritative position stream (the batched APPLY_POSITIONS teleport)
///     and produces the convergence flicker observed. Suppressing it makes convergence stable.
///
///   * Suppression is emitted ONCE per Sim on first sight in a mirrored snapshot, deduped by the
///     <see cref="_suppressed"/> set — NOT every snapshot. The mod's <c>APPLY_AUTONOMY</c> is an
///     idempotent absolute set, so a resend is harmless, but resending per Sim on every delta would
///     recreate the ~71-round-trip transport storm that batched APPLY_POSITIONS avoids. First-sight
///     dedupe keeps the per-snapshot mirror cost flat.
///
///   * Un-suppression is rare and event-driven, never absence-driven. A <c>SnapshotDelta</c> only
///     carries CHANGED Sims, so a Sim absent from a delta is "unchanged / idle", NOT "despawned" —
///     un-suppressing on absence would wake a townie's local AI and re-open the flicker. The only
///     legitimate un-suppress is a Sim leaving the mirrored set entirely: this client CLAIMS it
///     (mirrored -> self-owned), or it is released back from a claim. <see cref="OnSimNoLongerMirrored"/>
///     emits the balancing <c>ApplyAutonomy(disabled=false)</c> AND clears the first-sight flag so a
///     released Sim re-suppresses on its next delta sight (symmetric to <c>AutonomyCommandGate.Clear</c>).
///
///   * Interaction REPLAY from the snapshot's <see cref="SimSnapshot.CurrentInteractionKey"/> is
///     intentionally DEFERRED for the snapshot path. The wire carries only the Sim's CURRENT position,
///     which the APPLY_POSITIONS batch consumes as a teleport in the same simulator tick — so a
///     snapshot-driven GoHere toward that same current position is a zero-length route that completes
///     instantly and animates nothing. The real GoHere endpoint is only available on the EVENT path
///     (<c>ServerEvent.InteractionQueued</c> carries a real <c>TargetPosition</c>), which already emits
///     <c>ApplyInteractionQueue</c> via <see cref="EventToBridgeCommandTranslator"/>. <see cref="ClassifyInteractionKey"/>
///     encodes the replay-class decision (and is exercised by tests) so the snapshot path knows GoHere
///     is the replayable class, but deliberately does not act on it here. Full snapshot-driven replay
///     needs the server to put the interaction TARGET on the wire — a future, larger slice.
/// </summary>
public sealed class MirrorAutonomyController
{
    // Sims for which the client has CURRENTLY emitted one outstanding ApplyAutonomy(disabled=true).
    // Membership == "we are responsible for one outstanding suppression on this mirrored Sim". This
    // is the controller's source of truth for first-sight dedupe and leak-free un-suppress.
    private readonly HashSet<ulong> _suppressed = new();

    // Sims absent from the client sandbox (ApplyAutonomy rejected: Sim not found). Skip future
    // suppression attempts so ephemeral host-only townies do not wedge the bridge every delta.
    private readonly HashSet<ulong> _autonomyUnavailable = new();

    /// <summary>True while the controller is currently suppressing the Sim's local autonomy.</summary>
    public bool IsSuppressed(ulong simObjectId) => _suppressed.Contains(simObjectId);

    /// <summary>Count of currently-suppressed mirrored Sims (introspection / tests).</summary>
    public int SuppressedCount => _suppressed.Count;

    /// <summary>
    /// pure replay-class decision: classify a snapshot's <see cref="SimSnapshot.CurrentInteractionKey"/>.
    /// The mod populates the key as the interaction definition's <c>GetType().FullName</c>
    /// (e.g. <c>Sims3.Gameplay.Core.Terrain+GoHere</c>); the bare <c>Terrain.GoHere</c> form is also
    /// accepted to match the mod's own <c>HandleApplyInteractionQueue</c> key gate. Null/empty -> None.
    /// </summary>
    public static MirrorReplayClass ClassifyInteractionKey(string? currentInteractionKey)
    {
        if (string.IsNullOrEmpty(currentInteractionKey))
        {
            return MirrorReplayClass.None;
        }

        // Mirror the mod's HandleApplyInteractionQueue acceptance test exactly:
        //   interactionKey == "Terrain.GoHere" || interactionKey.Contains("Terrain+GoHere")
        if (currentInteractionKey == "Terrain.GoHere"
            || currentInteractionKey.Contains("Terrain+GoHere", StringComparison.Ordinal))
        {
            return MirrorReplayClass.GoHere;
        }

        return MirrorReplayClass.None;
    }

    /// <summary>
    /// Observe the Sims present in a snapshot the client is mirroring (self-owned Sims have ALREADY
    /// been stripped upstream by <see cref="MirrorEventFilter.FilterSnapshotSims"/>, so every Sim here
    /// is a mirrored Sim). Returns one <see cref="BridgeCommand.ApplyAutonomy"/> (disabled=true) per Sim
    /// seen for the FIRST time; already-suppressed Sims emit nothing (first-sight dedupe). Order follows
    /// snapshot order.
    /// </summary>
    public IReadOnlyList<BridgeCommand> OnMirroredSnapshotSims(IReadOnlyList<SimSnapshot> mirroredSims)
    {
        ArgumentNullException.ThrowIfNull(mirroredSims);
        if (mirroredSims.Count == 0)
        {
            return Array.Empty<BridgeCommand>();
        }

        List<BridgeCommand> commands = new();
        foreach (SimSnapshot sim in mirroredSims)
        {
            if (_autonomyUnavailable.Contains(sim.ObjectId))
            {
                continue;
            }

            if (_suppressed.Add(sim.ObjectId))
            {
                commands.Add(new BridgeCommand.ApplyAutonomy(Guid.NewGuid(), sim.ObjectId, Disabled: true));
            }
        }

        return commands.Count == 0 ? Array.Empty<BridgeCommand>() : commands;
    }

    /// <summary>
    /// A Sim has left the mirrored set — this client CLAIMED it (mirrored -> self-owned) or a claim was
    /// RELEASED. If the controller had it suppressed, returns the balancing
    /// <see cref="BridgeCommand.ApplyAutonomy"/> (disabled=false) so no client-side suppression leaks
    /// onto a now-owned Sim, and clears the first-sight flag so a released Sim re-suppresses on its next
    /// delta sight. Returns null when the Sim was not suppressed (nothing to unwind). Idempotent.
    /// </summary>
    public BridgeCommand? OnSimNoLongerMirrored(ulong simObjectId)
    {
        _autonomyUnavailable.Remove(simObjectId);

        if (_suppressed.Remove(simObjectId))
        {
            return new BridgeCommand.ApplyAutonomy(Guid.NewGuid(), simObjectId, Disabled: false);
        }

        return null;
    }

    /// <summary>
    /// Client sandbox has no Sim for this object id — stop emitting ApplyAutonomy for it.
    /// Clears a pending first-sight suppression so a later sight after a failed apply does not retry.
    /// </summary>
    /// <summary>
    /// W2: release every outstanding suppression (one ApplyAutonomy(disabled=false) per suppressed
    /// Sim) and forget them, so the engine refcount on the client returns to what it was before the
    /// controller touched it. Used when the client authority gate takes over (Enforce).
    /// </summary>
    public IReadOnlyList<BridgeCommand> ReleaseAll()
    {
        if (_suppressed.Count == 0)
        {
            return Array.Empty<BridgeCommand>();
        }

        List<BridgeCommand> commands = new(_suppressed.Count);
        foreach (ulong simObjectId in _suppressed)
        {
            commands.Add(new BridgeCommand.ApplyAutonomy(Guid.NewGuid(), simObjectId, Disabled: false));
        }

        _suppressed.Clear();
        return commands;
    }

    public void MarkAutonomyUnavailable(ulong simObjectId)
    {
        _autonomyUnavailable.Add(simObjectId);
        _suppressed.Remove(simObjectId);
    }
}
