using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// M4 P4.2.1 — pure-function translator from authoritative <see cref="ServerEvent"/>s to
/// <see cref="BridgeCommand"/>s. The client-side mirroring engine receives ServerEvents over the
/// public TCP protocol and feeds them through this translator to produce the bridge commands that
/// drive a local client-role TS3 instance via the memory bridge.
///
/// This class is intentionally pure (no I/O, no state, no logging). The wiring into
/// <see cref="SimsOnline.DedicatedServer"/> client code (memory bridge + send loop) is P4.2.2,
/// a separate slice.
///
/// Translation table (current support):
/// <list type="table">
/// <item><term><see cref="ServerEvent.FullSnapshot"/></term><description>One or more batched <see cref="BridgeCommand.ApplyPositions"/> carrying every Sim's position, chunked to the single-frame transport budget — replaces the former one-<see cref="BridgeCommand.ApplyPosition"/>-per-Sim fan-out that collapsed a ~71-Sim world into ~71 serial round-trips. Authoritative speed is mirrored by <see cref="MirrorEngine"/> (P5.5), not this translator.</description></item>
/// <item><term><see cref="ServerEvent.SnapshotDelta"/></term><description>Same as FullSnapshot — snapshot deltas only carry changed entities.</description></item>
/// <item><term><see cref="ServerEvent.InteractionQueued"/></term><description>Empty for now. The event does not carry the target position required by <c>APPLY_INTERACTION_QUEUE</c> for <c>Terrain.GoHere</c>. Wire-schema extension needed first.</description></item>
/// <item><term><see cref="ServerEvent.GameSpeedChanged"/></term><description>One <see cref="BridgeCommand.SetGameSpeed"/> — clients visually sync to the server-decided speed (P5.0 time/speed authority).</description></item>
/// <item><term><see cref="ServerEvent.WorldTimeChanged"/></term><description>One <see cref="BridgeCommand.SetWorldTime"/> — mirror clients apply the server's absolute clock.</description></item>
/// <item><term>All other events</term><description>Empty (control plane: session lifecycle, command acks, role changes — no local TS3 state to mirror).</description></item>
/// </list>
/// </summary>
public static class EventToBridgeCommandTranslator
{
    private static readonly IReadOnlyList<BridgeCommand> Empty = Array.Empty<BridgeCommand>();

    public static IReadOnlyList<BridgeCommand> Translate(ServerEvent serverEvent)
    {
        return serverEvent switch
        {
            ServerEvent.FullSnapshot full => TranslateSnapshot(full.Snapshot),
            ServerEvent.SnapshotDelta delta => TranslateSnapshot(delta.Snapshot),
            ServerEvent.SimAutonomyChanged sac => TranslateAutonomyChange(sac),
            // M4 P4.2.3 Oracle blocker 6: gate on TargetPosition != null only because script DLL
            // HandleApplyInteractionQueue requires hasPosition=1 for Terrain.GoHere (rejects with "Terrain.GoHere
            // requires a target position" otherwise). The mixed (TargetObjectId set, TargetPosition null) case
            // is walk-to-target replay: resolve GameObject.Position on the client at apply time.
            ServerEvent.InteractionQueued iq when !SimsOnlineServerHost.TerminalInteractionFilter.IsNeverReplay(iq.InteractionKey)
                && iq.TargetPosition != null => TranslateInteractionQueued(iq),
            ServerEvent.InteractionQueued iq when !SimsOnlineServerHost.TerminalInteractionFilter.IsNeverReplay(iq.InteractionKey)
                && iq.TargetObjectId is ulong tid && tid != 0 => TranslateInteractionQueued(iq),
            ServerEvent.InteractionReplayQueued replayQueued => TranslateInteractionReplayQueued(replayQueued.Bundle),
            // P5.0 time/speed authority: the server's GameSpeedChanged broadcast drives the
            // local client TS3's speed via the existing ungated SET_GAME_SPEED bridge command.
            ServerEvent.GameSpeedChanged gsc => new BridgeCommand[]
            {
                new BridgeCommand.SetGameSpeed(Guid.NewGuid(), gsc.Speed)
            },
            ServerEvent.WorldTimeChanged wtc => new BridgeCommand[]
            {
                new BridgeCommand.SetWorldTime(Guid.NewGuid(), wtc.Time)
            },
            // motive/cash/skill emission through the existing APPLY_* bridge commands.
            ServerEvent.SimMotiveChanged smc => new BridgeCommand[]
            {
                new BridgeCommand.ApplyMotive(Guid.NewGuid(), smc.SimObjectId, smc.MotiveName, smc.Value)
            },
            // W2 part B: visible state of a Sim this client does not own.
            ServerEvent.SimStateChanged ssc => new BridgeCommand[]
            {
                new BridgeCommand.ApplySimState(Guid.NewGuid(), new SimStateSample(ssc.SimObjectId, ssc.Motives, ssc.Buffs))
            },
            // W2 Sim identity step 3: server-driven spawn and despawn.
            ServerEvent.SimSpawned spawned => new BridgeCommand[]
            {
                new BridgeCommand.SpawnSim(Guid.NewGuid(), spawned.SimObjectId, spawned.SimDescriptionId, spawned.Position)
            },
            ServerEvent.SimDespawned despawned => new BridgeCommand[]
            {
                new BridgeCommand.DespawnSim(Guid.NewGuid(), despawned.SimObjectId, despawned.SimDescriptionId)
            },
            // step 5: a claimed Sim's job, funds and skills.
            ServerEvent.SimDetailsChanged details => new BridgeCommand[]
            {
                new BridgeCommand.ApplySimDetails(
                    Guid.NewGuid(),
                    new SimDetailsSample(details.SimObjectId, details.CareerGuid, details.CareerLevel, details.CareerBranch, details.Funds, details.Skills))
            },
            // a description this client asked for.
            ServerEvent.SimDescriptionData data => new BridgeCommand[]
            {
                new BridgeCommand.ImportSimDescription(
                    Guid.NewGuid(),
                    new SimDescriptionPayload(data.SimDescriptionId, data.PropertyStream, data.ResKeyTable, data.ObjectIdTable))
            },
            // presentation stream: one SACS_REPLAY per bridge frame.
            ServerEvent.SimAnimation animation => PresentationStream.ReplayBatches(animation.Lines)
                .Select(batch => (BridgeCommand)new BridgeCommand.SacsReplay(Guid.NewGuid(), batch))
                .ToArray(),
            ServerEvent.HouseholdFundsChanged hfc => new BridgeCommand[]
            {
                new BridgeCommand.ApplyCash(Guid.NewGuid(), hfc.HouseholdId, hfc.Funds)
            },
            ServerEvent.SimSkillChanged ssc => new BridgeCommand[]
            {
                new BridgeCommand.ApplySkill(Guid.NewGuid(), ssc.SimObjectId, ssc.SkillName, ssc.Level)
            },
            _ => Empty
        };
    }

    // emit batched APPLY_POSITIONS instead of one APPLY_POSITION per Sim. A ~71-Sim
    // snapshot becomes one (or two) framed commands the mod applies in a single simulator tick,
    // rather than ~71 serial bridge round-trips that the single 4 KB frame could not drain in time
    // (the host↔client divergence in). The pairs are chunked to the per-frame budget by
    // BridgeLineCodec.ChunkApplyPositions; each chunk is one independent command with its own OK.
    private static IReadOnlyList<BridgeCommand> TranslateSnapshot(SimulationSnapshot snapshot)
    {
        if (snapshot.Sims.Count == 0)
        {
            return Empty;
        }

        List<(ulong SimObjectId, Vector3Dto Position)> pairs = new(snapshot.Sims.Count);
        foreach (SimSnapshot sim in snapshot.Sims)
        {
            pairs.Add((sim.ObjectId, sim.Position));
        }

        IReadOnlyList<BridgeCommand.ApplyPositions> chunks = BridgeLineCodec.ChunkApplyPositions(pairs);
        List<BridgeCommand> commands = new(chunks.Count);
        foreach (BridgeCommand.ApplyPositions chunk in chunks)
        {
            commands.Add(chunk);
        }
        return commands;
    }

    private static IReadOnlyList<BridgeCommand> TranslateAutonomyChange(ServerEvent.SimAutonomyChanged sac)
    {
        return new BridgeCommand[]
        {
            new BridgeCommand.ApplyAutonomy(Guid.NewGuid(), sac.SimObjectId, sac.Disabled)
        };
    }

    /// <summary>true when the event can become a client-role interaction replay command.</summary>
    public static bool IsInteractionReplayCandidate(ServerEvent.InteractionQueued iq)
    {
        if (SimsOnlineServerHost.TerminalInteractionFilter.IsNeverReplay(iq.InteractionKey))
        {
            return false;
        }

        if (iq.TargetPosition != null)
        {
            return true;
        }

        return iq.TargetObjectId is ulong targetId && targetId != 0;
    }

    /// <summary>map a replay candidate to the wire interaction request shape. BQ: carries
    /// the event's semantic parameter map (bookId, progressNormalized, seat*) when the source
    /// stamped it, instead of the former always-empty dict.</summary>
    public static InteractionRequest ToInteractionRequest(ServerEvent.InteractionQueued iq) =>
        new(
            iq.ActorObjectId,
            iq.InteractionKey,
            iq.TargetObjectId,
            iq.TargetPosition,
            iq.Parameters ?? new Dictionary<string, string>());

    public static bool IsInteractionReplayCandidate(InteractionReplayBundle bundle)
    {
        InteractionReplayDecision decision = InteractionReplayPolicyResolver.Resolve(bundle);
        return decision.Policy is ReplayPolicy.ReplayExact or ReplayPolicy.ReplayAsRoute
            && (bundle.TargetPosition != null || bundle.TargetObjectId is ulong targetId && targetId != 0);
    }

    /// <summary>ForceAlign bundles with a real seat/container (not the sim's own id).</summary>
    public static bool IsForceAlignCandidate(InteractionReplayBundle bundle) =>
        GetForceAlignSeatObjectId(bundle) != null;

    /// <summary>
    /// Authoritative seat for ForceAlign. Prefer postureContainer when it is a real object;
    /// transition Sit is often captured while still StandingPosture (container == actor) but
    /// targetObjectId already names the loveseat/chair.
    /// </summary>
    public static ulong? GetForceAlignSeatObjectId(InteractionReplayBundle bundle)
    {
        InteractionReplayDecision decision = InteractionReplayPolicyResolver.Resolve(bundle);
        if (decision.Policy != ReplayPolicy.ForceAlign)
        {
            return null;
        }

        if (bundle.PostureContainerObjectId is ulong containerId
            && containerId != 0
            && containerId != bundle.ActorObjectId)
        {
            return containerId;
        }

        if (SimsOnlineServerHost.SitTransitionEmitFilter.IsSitTransitionReplay(bundle.InteractionKey)
            && bundle.TargetObjectId is ulong targetId
            && targetId != 0
            && targetId != bundle.ActorObjectId)
        {
            return targetId;
        }

        return null;
    }

    /// <summary>Seat-normalized bundle for APPLY_FORCE_ALIGN dispatch.</summary>
    public static InteractionReplayBundle NormalizeForceAlignBundleForDispatch(InteractionReplayBundle bundle) =>
        NormalizeForceAlignBundle(bundle);

    private static InteractionReplayBundle NormalizeForceAlignBundle(InteractionReplayBundle bundle)
    {
        ulong? seatId = GetForceAlignSeatObjectId(bundle);
        if (!seatId.HasValue)
        {
            return bundle;
        }

        return bundle with { PostureContainerObjectId = seatId.Value };
    }

    /// <summary>
    /// Map a replay bundle to the wire interaction request. when the resolver
    /// decides <see cref="ReplayPolicy.ReplayAsRoute"/>, synthesize mod-accepted
    /// <c>Terrain.GoHere</c> (walk-to-target or walk-to-position) instead of forwarding the raw
    /// unknown family key that hard-rejects on APPLY_INTERACTION_QUEUE.
    /// </summary>
    public static InteractionRequest ToInteractionRequest(InteractionReplayBundle bundle)
    {
        InteractionReplayDecision decision = InteractionReplayPolicyResolver.Resolve(bundle);
        if (decision.Policy == ReplayPolicy.ReplayAsRoute)
        {
            return ToRouteDegradedInteractionRequest(bundle);
        }

        return new(
            bundle.ActorObjectId,
            bundle.InteractionKey,
            bundle.TargetObjectId,
            bundle.TargetPosition,
            bundle.Parameters ?? new Dictionary<string, string>());
    }

    /// <summary>
    /// explicit GoHere shape for ReplayAsRoute degrade (empty Parameters — the mod
    /// HandleApplyInteractionQueue ignores them for Terrain.GoHere).
    /// </summary>
    public static InteractionRequest ToRouteDegradedInteractionRequest(InteractionReplayBundle bundle) =>
        new(
            bundle.ActorObjectId,
            "Terrain.GoHere",
            bundle.TargetObjectId,
            bundle.TargetPosition,
            new Dictionary<string, string>());

    /// <summary>True when the interaction key is already a Terrain.GoHere family the mod accepts.</summary>
    public static bool IsAlreadyGoHereKey(string? interactionKey) =>
        !string.IsNullOrEmpty(interactionKey)
        && (interactionKey == "Terrain.GoHere"
            || interactionKey.Contains("Terrain+GoHere", StringComparison.Ordinal));

    /// <summary>
    /// true when ReplayAsRoute will replace the original key with Terrain.GoHere
    /// (unknown / social-route / non-GoHere routing families).
    /// </summary>
    public static bool IsRouteDegraded(InteractionReplayBundle bundle)
    {
        InteractionReplayDecision decision = InteractionReplayPolicyResolver.Resolve(bundle);
        return decision.Policy == ReplayPolicy.ReplayAsRoute
            && !IsAlreadyGoHereKey(bundle.InteractionKey);
    }

    /// <summary>
    /// collapse N interaction replays into one or more batched
    /// <see cref="BridgeCommand.ApplyInteractions"/> frames (chunked to the 4080-byte budget).
    /// </summary>
    public static IReadOnlyList<BridgeCommand> TranslateInteractionBatch(
        IReadOnlyList<ServerEvent.InteractionQueued> interactions)
    {
        if (interactions == null || interactions.Count == 0)
        {
            return Empty;
        }

        List<InteractionRequest> requests = new(interactions.Count);
        foreach (ServerEvent.InteractionQueued iq in interactions)
        {
            if (IsInteractionReplayCandidate(iq))
            {
                requests.Add(ToInteractionRequest(iq));
            }
        }

        return TranslateInteractionRequestBatch(requests);
    }

    /// <summary>shared batch encoder for InteractionQueued and replay-bundle book chains.</summary>
    public static IReadOnlyList<BridgeCommand> TranslateInteractionRequestBatch(
        IReadOnlyList<InteractionRequest> requests)
    {
        if (requests == null || requests.Count == 0)
        {
            return Empty;
        }

        if (requests.Count == 1)
        {
            return new BridgeCommand[]
            {
                new BridgeCommand.ApplyInteractionQueue(Guid.NewGuid(), requests[0])
            };
        }

        IReadOnlyList<BridgeCommand.ApplyInteractions> chunks = BridgeLineCodec.ChunkApplyInteractions(requests);
        List<BridgeCommand> commands = new(chunks.Count);
        foreach (BridgeCommand.ApplyInteractions chunk in chunks)
        {
            commands.Add(chunk);
        }

        return commands;
    }

    /// <summary>M4 P4.2.3: synthesise a single ApplyInteractionQueue from a target-bearing InteractionQueued event. Empty Parameters dict (script DLL HandleApplyInteractionQueue ignores Parameters for Terrain.GoHere). Translator gate already filtered for TargetPosition != null.</summary>
    private static IReadOnlyList<BridgeCommand> TranslateInteractionQueued(ServerEvent.InteractionQueued iq)
    {
        return TranslateInteractionBatch(new[] { iq });
    }

    private static IReadOnlyList<BridgeCommand> TranslateInteractionReplayQueued(InteractionReplayBundle bundle)
    {
        InteractionReplayDecision decision = InteractionReplayPolicyResolver.Resolve(bundle);
        if (decision.Policy == ReplayPolicy.NeverReplay)
        {
            return Empty;
        }

        if (IsForceAlignCandidate(bundle))
        {
            return new BridgeCommand[]
            {
                new BridgeCommand.ApplyForceAlign(Guid.NewGuid(), NormalizeForceAlignBundle(bundle))
            };
        }

        // SlideOnly is an explicit policy decision (position stream carries the slide),
        // not a silent unclassified drop. MirrorEngine emits degrade telemetry for this arm.
        if (decision.Policy == ReplayPolicy.SlideOnly)
        {
            return Empty;
        }

        if (!IsInteractionReplayCandidate(bundle))
        {
            return Empty;
        }

        return new BridgeCommand[]
        {
            new BridgeCommand.ApplyInteractionQueue(Guid.NewGuid(), ToInteractionRequest(bundle))
        };
    }
}