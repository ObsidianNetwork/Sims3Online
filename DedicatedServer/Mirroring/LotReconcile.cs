using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Mirroring;

/// <summary>
/// slice 6: checkpoint policy when a client-native player has no reconciled lot export.
/// Default is the design-recommended reject (docs/archive/dev-149-slice5-partial-save-reconciliation.md,
/// trigger 1): checkpointing server state while solo_native/prox_mirror live mutations exist only
/// in the client TS3 is the "owned state lost on reload" FAIL class.
/// </summary>
public enum LotReconcileSavePolicy
{
    /// <summary>Block SAVE_GAME with CommandRejected until every client-native player reconciled.</summary>
    RejectWhenUnreconciled = 0,

    /// <summary>Accept the checkpoint with documented data-loss risk (operator opt-in).</summary>
    AllowWithDataLossRisk = 1,
}

/// <summary> slice 6: product constants for the lot reconcile boundary.</summary>
public static class LotReconcilePolicy
{
    /// <summary>
    /// A merged export older than this no longer satisfies the checkpoint gate — the client
    /// keeps simulating natively, so a stale merge re-opens the dual-save divergence window.
    /// Sized to a few broadcast/export round-trips; checkpoint cadence is minutes.
    /// </summary>
    public static readonly TimeSpan ExportFreshnessWindow = TimeSpan.FromSeconds(30);

    /// <summary>Minimum spacing between LotExportRequested pushes to the same player.</summary>
    public static readonly TimeSpan ExportRequestInterval = TimeSpan.FromSeconds(10);
}

/// <summary>
/// slice 5: the seven lot-resident ExportContent entry classes the merge
/// tracks per-field last-mutated ticks for (design doc §3.2). Position is the S-SPIKE-proven
/// field the existing MERGE_LOT_STATE surface applies; the others ride along so the
/// conflict detector can classify client-wins / server-wins / contested without a per-tick
/// scan. Seven slots per lot-resident Sim, not per-tick.
/// </summary>
public enum LotReconcileFieldKind
{
    SimDescription = 0,
    Relationships = 1,
    Inventory = 2,
    Funds = 3,
    Objects = 4,
    Opportunities = 5,
    Rewards = 6,
}

/// <summary>
/// slice 5: per-(player, lot) server-side last-mutated tick for each of the
/// seven lot-resident ExportContent entry classes. The controller records its own mutation
/// tick whenever the server TS3 changes a lot-resident field (SP, townie-on-lot, economy
/// fallback). Thin map: 7 slots per lot-resident Sim, not per-tick. Used by
/// <see cref="LotReconcileCoordinator"/> to classify contested fields.
/// </summary>
public sealed class LotReconcileFieldTickMap
{
    private readonly Dictionary<(int PlayerId, ulong LotId, LotReconcileFieldKind Field), long> _ticks = new();

    // slice 5 (design §3.2): per-(player, lot, field) last-reconciled tick,
    // advanced after each accepted merge to max(clientTick, serverTick). ClassifyField compares
    // both the client and server mutation ticks against THIS baseline, not a hardcoded 0.
    // Without it, a server mutation recorded before the first reconcile keeps classifying as
    // "serverMutated" forever, even after the client has acknowledged the merge.
    private readonly Dictionary<(int PlayerId, ulong LotId, LotReconcileFieldKind Field), long> _lastReconciledTicks = new();

    /// <summary>Record the server-side last-mutated tick for a (player, lot, field) triple.</summary>
    public void Record(int playerId, ulong lotId, LotReconcileFieldKind field, long tick)
    {
        _ticks[(playerId, lotId, field)] = tick;
    }

    /// <summary>Latest recorded server-side tick for a (player, lot, field) triple; 0 if never recorded.</summary>
    public long GetLastMutatedTick(int playerId, ulong lotId, LotReconcileFieldKind field)
    {
        return _ticks.TryGetValue((playerId, lotId, field), out long tick) ? tick : 0L;
    }

    /// <summary>
    /// record the last-reconciled tick for a (player, lot, field) triple after a
    /// successful merge. Should be set to max(clientTick, serverTick) so both sides' "mutated
    /// since last reconcile" predicates reset to false.
    /// </summary>
    public void RecordReconciledTick(int playerId, ulong lotId, LotReconcileFieldKind field, long tick)
    {
        _lastReconciledTicks[(playerId, lotId, field)] = tick;
    }

    /// <summary>the last-reconciled tick for a (player, lot, field) triple; 0 if never set.</summary>
    public long GetLastReconciledTick(int playerId, ulong lotId, LotReconcileFieldKind field)
    {
        return _lastReconciledTicks.TryGetValue((playerId, lotId, field), out long tick) ? tick : 0L;
    }

    /// <summary>Clear all field ticks for a player (leave/disconnect cleanup).</summary>
    public void Clear(int playerId)
    {
        // Collect keys first, then remove: mutating a Dictionary during foreach over its
        // enumerator invalidates it and throws InvalidOperationException on the next MoveNext
        // once the player has any recorded entry. LeaveSession/disconnect hits this path.
        List<(int PlayerId, ulong LotId, LotReconcileFieldKind Field)> keysToRemove = new();
        foreach (KeyValuePair<(int PlayerId, ulong LotId, LotReconcileFieldKind Field), long> entry in _ticks)
        {
            if (entry.Key.PlayerId == playerId)
            {
                keysToRemove.Add(entry.Key);
            }
        }

        foreach ((int PlayerId, ulong LotId, LotReconcileFieldKind Field) key in keysToRemove)
        {
            _ticks.Remove(key);
        }

        List<(int PlayerId, ulong LotId, LotReconcileFieldKind Field)> reconciledKeysToRemove = new();
        foreach (KeyValuePair<(int PlayerId, ulong LotId, LotReconcileFieldKind Field), long> entry in _lastReconciledTicks)
        {
            if (entry.Key.PlayerId == playerId)
            {
                reconciledKeysToRemove.Add(entry.Key);
            }
        }
        foreach ((int PlayerId, ulong LotId, LotReconcileFieldKind Field) key in reconciledKeysToRemove)
        {
            _lastReconciledTicks.Remove(key);
        }
    }
}

/// <summary>
/// slice 6 + slice 5: per-player lot export bookkeeping for the three reconcile
/// triggers (checkpoint, authority-mode exit, disconnect/leave). Extended with the monotonic
/// checkpoint version (design §3.1) and the last-reconciled snapshot reference for the
/// relationships full-diff fallback (design §3.3). Not thread-safe by itself — the session
/// serialises access through its command gate.
/// </summary>
public sealed class LotReconcileTracker
{
    private sealed class Entry
    {
        public required LotExportBundle Bundle;
        public DateTimeOffset ReceivedAtUtc;
        public bool Merged;
        /// <summary>client-reported lastReconciledVersion carried in the export request.</summary>
        public long ClientLastReconciledVersion;
    }

    private readonly Dictionary<int, Entry> _byPlayer = new();
    private readonly Dictionary<int, DateTimeOffset> _lastExportRequestUtc = new();
    private readonly HashSet<int> _outstandingExportRequests = new();

    /// <summary>
    /// slice 5 (design §3.1): monotonic checkpoint version per (player, lot),
    /// incremented on every accepted merge. Persisted in the controller; the client echoes it
    /// back in the next export request as <c>lastReconciledVersion</c>.
    /// </summary>
    private readonly Dictionary<(int PlayerId, ulong LotId), long> _currentVersion = new();

    /// <summary>
    /// slice 5 (design §3.3): the last-merged lot snapshot per (player, lot),
    /// used as the diff baseline for the relationships full-diff fallback.
    /// </summary>
    private readonly Dictionary<(int PlayerId, ulong LotId), LotReconcileSnapshot> _lastSnapshot = new();

    /// <summary>Record a validated export as the player's latest (unmerged until <see cref="MarkMerged"/>).</summary>
    public void Record(int playerId, LotExportBundle bundle, DateTimeOffset nowUtc)
    {
        _byPlayer[playerId] = new Entry
        {
            Bundle = bundle,
            ReceivedAtUtc = nowUtc,
            Merged = false,
            ClientLastReconciledVersion = 0L,
        };
    }

    /// <summary>
    /// record a validated export with the client-reported lastReconciledVersion
    /// (design §3.1). The version lets the coordinator classify a field as contestable only if
    /// <c>client.lastReconciledVersion &lt; server.currentVersion</c>.
    /// </summary>
    public void Record(int playerId, LotExportBundle bundle, DateTimeOffset nowUtc, long clientLastReconciledVersion)
    {
        _byPlayer[playerId] = new Entry
        {
            Bundle = bundle,
            ReceivedAtUtc = nowUtc,
            Merged = false,
            ClientLastReconciledVersion = clientLastReconciledVersion,
        };
    }

    public void MarkMerged(int playerId)
    {
        if (_byPlayer.TryGetValue(playerId, out Entry? entry))
        {
            entry.Merged = true;
        }
    }

    /// <summary>Latest stored export that has not yet been merged into the server TS3.</summary>
    public bool TryGetPendingMerge(int playerId, out LotExportBundle bundle)
    {
        if (_byPlayer.TryGetValue(playerId, out Entry? entry) && !entry.Merged)
        {
            bundle = entry.Bundle;
            return true;
        }

        bundle = null!;
        return false;
    }

    /// <summary>
    /// the client-reported lastReconciledVersion for the player's latest export, or 0
    /// if no export is stored. Used by the coordinator to detect version mismatch (design §3.1).
    /// </summary>
    public long GetClientLastReconciledVersion(int playerId)
    {
        return _byPlayer.TryGetValue(playerId, out Entry? entry) ? entry.ClientLastReconciledVersion : 0L;
    }

    /// <summary>
    /// the server's current monotonic checkpoint version for the (player, lot) pair.
    /// Incremented by <see cref="BumpVersion"/> on every accepted merge.
    /// </summary>
    public long GetCurrentVersion(int playerId, ulong lotId)
    {
        return _currentVersion.TryGetValue((playerId, lotId), out long v) ? v : 0L;
    }

    /// <summary>increment the server's monotonic checkpoint version for the (player, lot) pair.</summary>
    public long BumpVersion(int playerId, ulong lotId)
    {
        long next = (_currentVersion.TryGetValue((playerId, lotId), out long v) ? v : 0L) + 1L;
        _currentVersion[(playerId, lotId)] = next;
        return next;
    }

    /// <summary>
    /// record the last-merged lot snapshot for the (player, lot) pair. Used as the diff
    /// baseline for the relationships full-diff fallback (design §3.3).
    /// </summary>
    public void RecordSnapshot(int playerId, ulong lotId, LotReconcileSnapshot snapshot)
    {
        _lastSnapshot[(playerId, lotId)] = snapshot;
    }

    /// <summary>the last-merged lot snapshot for the (player, lot) pair, or null if none.</summary>
    public LotReconcileSnapshot? GetSnapshot(int playerId, ulong lotId)
    {
        return _lastSnapshot.TryGetValue((playerId, lotId), out LotReconcileSnapshot? snap) ? snap : null;
    }

    /// <summary>
    /// Checkpoint gate: the player counts as reconciled when their latest export was merged
    /// into the server TS3 within the freshness window.
    /// </summary>
    public bool IsReconciledForCheckpoint(int playerId, DateTimeOffset nowUtc, TimeSpan freshnessWindow)
    {
        return _byPlayer.TryGetValue(playerId, out Entry? entry)
            && entry.Merged
            && nowUtc - entry.ReceivedAtUtc <= freshnessWindow;
    }

    /// <summary>
    /// Debounced LotExportRequested gate: true (and stamps the request time) when the player
    /// has no fresh reconciled export and no request was pushed within the request interval.
    /// </summary>
    public bool ShouldRequestExport(int playerId, DateTimeOffset nowUtc, TimeSpan freshnessWindow, TimeSpan requestInterval)
    {
        if (IsReconciledForCheckpoint(playerId, nowUtc, freshnessWindow))
        {
            return false;
        }

        if (_lastExportRequestUtc.TryGetValue(playerId, out DateTimeOffset last) && nowUtc - last < requestInterval)
        {
            return false;
        }

        _lastExportRequestUtc[playerId] = nowUtc;
        _outstandingExportRequests.Add(playerId);
        return true;
    }

    /// <summary>Consume the server-request marker that requires deep-export quarantine semantics.</summary>
    public bool ConsumeOutstandingExportRequest(int playerId)
    {
        return _outstandingExportRequests.Remove(playerId);
    }

    /// <summary>Drop all state for a player (leave/disconnect cleanup).</summary>
    public void Clear(int playerId)
    {
        _byPlayer.Remove(playerId);
        _lastExportRequestUtc.Remove(playerId);
        _outstandingExportRequests.Remove(playerId);
        // Collect keys first, then remove: mutating a Dictionary during foreach over its
        // enumerator invalidates it and throws InvalidOperationException on the next MoveNext
        // once the player has any version/snapshot entry. LeaveSession calls this after a merge.
        List<(int PlayerId, ulong LotId)> versionKeys = new();
        foreach (KeyValuePair<(int PlayerId, ulong LotId), long> entry in _currentVersion)
        {
            if (entry.Key.PlayerId == playerId)
            {
                versionKeys.Add(entry.Key);
            }
        }
        foreach ((int PlayerId, ulong LotId) key in versionKeys)
        {
            _currentVersion.Remove(key);
        }

        List<(int PlayerId, ulong LotId)> snapshotKeys = new();
        foreach (KeyValuePair<(int PlayerId, ulong LotId), LotReconcileSnapshot> entry in _lastSnapshot)
        {
            if (entry.Key.PlayerId == playerId)
            {
                snapshotKeys.Add(entry.Key);
            }
        }
        foreach ((int PlayerId, ulong LotId) key in snapshotKeys)
        {
            _lastSnapshot.Remove(key);
        }
    }
}

/// <summary>
/// slice 5 (design §3.3): the last-merged lot state for a (player, lot) pair,
/// used as the diff baseline for the relationships full-diff fallback. Holds the relationships
/// edges the server and client both held at the last reconcile; the coordinator diffs the next
/// export against this to classify edges as client-only / server-only / both.
/// </summary>
public sealed record LotReconcileSnapshot(
    int PlayerId,
    ulong LotId,
    long Version,
    IReadOnlyList<LotReconcileRelationshipEdge> Relationships);

/// <summary>
/// slice 5: one relationship edge in a <see cref="LotReconcileSnapshot"/>.
/// EndpointA is the owned lot-resident Sim; EndpointB is the other-side Sim (owned or townie).
/// Authority is the edge-endpoint rule from design §2.1 (client for owned-to-owned, server for
/// owned-to-townie).
/// </summary>
public sealed record LotReconcileRelationshipEdge(ulong EndpointA, ulong EndpointB, int Score, bool OwnedToOwned);

/// <summary>
/// slice 5 (design §3.1 + §3.2): the result of classifying a single field's
/// conflict state. <see cref="ClientWins"/> means only the client mutated since the last
/// reconcile (or the versions match, so the server has no competing mutation).
/// <see cref="ServerWins"/> means only the server mutated.
/// <see cref="Contested"/> means both sides mutated; the design's authority assignment
/// (§2.1) decides the winner.
/// </summary>
public enum LotReconcileFieldVerdict
{
    NoConflict = 0,
    ClientWins = 1,
    ServerWins = 2,
    Contested = 3,
}

/// <summary>
/// slice 5 (design §3): the per-field conflict classification for one
/// ExportContent entry class on one lot-resident Sim.
/// </summary>
public sealed record LotReconcileFieldResult(
    LotReconcileFieldKind Field,
    LotReconcileFieldVerdict Verdict,
    long ClientTick,
    long ServerTick,
    long LastReconciledTick);

/// <summary>
/// slice 5 (design §3 + §4): the full conflict-detection result for one export.
/// Carries the per-field verdicts and the merge plan the coordinator built from them.
/// </summary>
public sealed record LotReconcilePlan(
    int PlayerId,
    ulong LotId,
    long ClientLastReconciledVersion,
    long ServerCurrentVersion,
    IReadOnlyList<LotReconcileFieldResult> Fields,
    LotExportBundle FilteredBundle,
    bool VersionMismatch)
{
    /// <summary>True if any field is contested (both sides mutated since the last reconcile).</summary>
    public bool HasContestedFields
    {
        get
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                if (Fields[i].Verdict == LotReconcileFieldVerdict.Contested)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

/// <summary>
/// slice 5 (design §3 + §4): the merge orchestrator. Consumes the client lot
/// export, runs conflict detection (monotonic checkpoint version + per-field lastMutatedTick +
/// relationships full-diff fallback), applies the ownership-split policy (lot-resident =
/// client-auth, townies/community/economy = server-auth — preserved by scope: the client
/// export EXCLUDES townies/foreign-players/off-lot/global-SP), and emits the merge command
/// sequence (the existing MERGE_LOT_STATE + SAVE_GAME surface).
///
/// Stage 1 scope: the coordinator runs at the in-memory host abstraction level. The
/// <see cref="IAuthoritativeSimulationHost.MergeLotStateAsync"/> + <see cref="IAuthoritativeSimulationHost.SaveGameAsync"/>
/// calls are the existing slice-6 position-only surface. quarantines deep-state import
/// until a replacement lifecycle passes mandatory cold-reload validation.
/// </summary>
public static class LotReconcileCoordinator
{
    /// <summary>
    /// Run the full conflict-detection + ownership-policy pipeline on one export and return the
    /// merge plan. Does NOT invoke the host — the caller (<see cref="AuthoritativeSession"/>) drives
    /// the bridge from the plan. Design §4.3 steps 2–3.
    /// </summary>
    public static LotReconcilePlan BuildMergePlan(
        LotExportBundle filteredBundle,
        LotReconcileTracker tracker,
        LotReconcileFieldTickMap serverFieldTicks,
        long clientExportTime)
    {
        long serverVersion = tracker.GetCurrentVersion(filteredBundle.PlayerId, filteredBundle.LotId);
        long clientVersion = tracker.GetClientLastReconciledVersion(filteredBundle.PlayerId);
        bool versionMismatch = clientVersion < serverVersion;

        List<LotReconcileFieldResult> fields = new(7);
        // Position is the S-SPIKE-proven field MERGE_LOT_STATE applies. The other six ride along
        // for classification; at stage 1 they are all NoConflict (the existing surface does not
        // emit per-field ticks for them yet — that wiring lands with the ImportContent chain).
        // Position uses the export time as the client tick; the server tick comes from the field map.
        long serverPosTick = serverFieldTicks.GetLastMutatedTick(filteredBundle.PlayerId, filteredBundle.LotId, LotReconcileFieldKind.Objects);
        long lastReconciledTick = serverFieldTicks.GetLastReconciledTick(filteredBundle.PlayerId, filteredBundle.LotId, LotReconcileFieldKind.Objects);
        LotReconcileFieldVerdict posVerdict = ClassifyField(clientExportTime, serverPosTick, lastReconciledTick, versionMismatch);
        fields.Add(new LotReconcileFieldResult(LotReconcileFieldKind.Objects, posVerdict, clientExportTime, serverPosTick, lastReconciledTick));

        // The other six fields default to ClientWins (client-authoritative per §2.1) when there is
        // no server mutation known, which is the stage-1 safe default: the lot-scoped export boundary
        // (§2.2) means the client export cannot clobber class B state, so client-wins is safe.
        foreach (LotReconcileFieldKind kind in new[]
        {
            LotReconcileFieldKind.SimDescription,
            LotReconcileFieldKind.Relationships,
            LotReconcileFieldKind.Inventory,
            LotReconcileFieldKind.Funds,
            LotReconcileFieldKind.Opportunities,
            LotReconcileFieldKind.Rewards,
        })
        {
            long serverTick = serverFieldTicks.GetLastMutatedTick(filteredBundle.PlayerId, filteredBundle.LotId, kind);
            long fieldLastReconciledTick = serverFieldTicks.GetLastReconciledTick(filteredBundle.PlayerId, filteredBundle.LotId, kind);
            LotReconcileFieldVerdict v = ClassifyField(clientExportTime, serverTick, fieldLastReconciledTick, versionMismatch);
            fields.Add(new LotReconcileFieldResult(kind, v, clientExportTime, serverTick, fieldLastReconciledTick));
        }

        // Design §4.3 step 3: enforce the verdict. A ServerWins Objects field means the server
        // mutated the Sim's position since the last reconcile and the client did not — applying
        // the stale client position would overwrite the server-only mutation. Drop that Sim from
        // the apply bundle so the server-canonical position survives. The other six fields are
        // stage-1 classification-only (no ImportContent surface yet), so they are not stripped
        // here — only the Objects/position verdict gates the MERGE_LOT_STATE apply set.
        LotExportBundle enforcedBundle = posVerdict == LotReconcileFieldVerdict.ServerWins
            ? DropAllSims(filteredBundle)
            : filteredBundle;

        return new LotReconcilePlan(
            filteredBundle.PlayerId,
            filteredBundle.LotId,
            clientVersion,
            serverVersion,
            fields,
            enforcedBundle,
            VersionMismatch: versionMismatch);
    }

    /// <summary>
    /// Return a bundle with an empty Sim apply set, preserving the player/lot/tick metadata.
    /// Used when every Sim's position is server-winning so nothing should be applied.
    /// </summary>
    private static LotExportBundle DropAllSims(LotExportBundle bundle)
        => bundle with { Sims = Array.Empty<LotSimExport>() };

    /// <summary>
    /// Design §3.2 conflict predicate: a field is contestable only if both sides mutated it
    /// since the last reconcile. If only one side mutated, that side wins by ownership (no
    /// conflict). If both mutated, the verdict is Contested (§2's authority assignment decides
    /// the winner — for lot-resident owned entities, the client wins).
    /// </summary>
    private static LotReconcileFieldVerdict ClassifyField(long clientTick, long serverTick, long lastReconciledTick, bool versionMismatch)
    {
        bool clientMutated = clientTick > lastReconciledTick;
        bool serverMutated = serverTick > lastReconciledTick;

        if (!versionMismatch && !serverMutated)
        {
            // Versions match and server did not mutate: client wins by ownership (no conflict).
            return clientMutated ? LotReconcileFieldVerdict.ClientWins : LotReconcileFieldVerdict.NoConflict;
        }

        if (clientMutated && serverMutated)
        {
            return LotReconcileFieldVerdict.Contested;
        }

        if (clientMutated && !serverMutated)
        {
            return LotReconcileFieldVerdict.ClientWins;
        }

        if (!clientMutated && serverMutated)
        {
            return LotReconcileFieldVerdict.ServerWins;
        }

        return LotReconcileFieldVerdict.NoConflict;
    }
}

/// <summary>
/// slice 6: server-side class B protection. A submitted bundle is stripped to Sims the
/// player actually owns (claims) and that are on the declared lot per the controller's lot index,
/// so a client export can never patch foreign player Sims, townies, or off-lot state
/// ( invariant; conflict table "Global SP / off-lot: server always").
/// </summary>
public static class LotExportValidator
{
    public static LotExportBundle FilterToOwnedLot(
        LotExportBundle bundle,
        IReadOnlyDictionary<ulong, int> claims,
        IReadOnlyList<SimSnapshot> knownSims)
    {
        List<LotSimExport> kept = new(bundle.Sims.Count);
        bool indexPopulated = knownSims.Count > 0;
        foreach (LotSimExport sim in bundle.Sims)
        {
            if (!claims.TryGetValue(sim.ObjectId, out int owner) || owner != bundle.PlayerId)
            {
                continue;
            }

            if (indexPopulated)
            {
                // PR#15 review fix 3: when the controller lot index is populated, a Sim
                // absent from the index cannot be reconciled onto the declared lot — reject it.
                // Empty index (bootstrap before first snapshot) still accepts owned Sims on the
                // declared lot so the first export can land.
                if (!TryGetKnownLot(sim.ObjectId, knownSims, out ulong knownLot) || knownLot != bundle.LotId)
                {
                    continue;
                }
            }

            kept.Add(sim);
        }

        return bundle with { Sims = kept };
    }

    private static bool TryGetKnownLot(ulong simObjectId, IReadOnlyList<SimSnapshot> sims, out ulong lotId)
    {
        for (int i = 0; i < sims.Count; i++)
        {
            if (sims[i].ObjectId == simObjectId && sims[i].LotId is ulong id)
            {
                lotId = id;
                return true;
            }
        }

        lotId = 0;
        return false;
    }
}

/// <summary>
/// slice 6: client-side class A extraction. The lot export is derived from the client
/// TS3's own snapshot (the existing REQUEST_SNAPSHOT bridge round-trip is the export transport —
/// no new client-side bridge command), filtered to the player's owned Sims on their active lot.
/// Foreign player Sims and townies never enter the bundle (they reconcile from their own
/// authority: owning client or server canonical).
/// </summary>
public static class LotExportBuilder
{
    public static LotExportBundle? BuildFromSnapshot(
        int playerId,
        IReadOnlySet<ulong> ownedSims,
        ulong? ownedActiveLotId,
        SimulationSnapshot clientSnapshot)
    {
        if (ownedSims.Count == 0 || clientSnapshot.Sims.Count == 0)
        {
            return null;
        }

        ulong? lotId = ownedActiveLotId;
        if (lotId == null)
        {
            foreach (SimSnapshot sim in clientSnapshot.Sims)
            {
                if (ownedSims.Contains(sim.ObjectId) && sim.LotId is ulong simLot)
                {
                    lotId = simLot;
                    break;
                }
            }
        }

        if (lotId is not ulong exportLot)
        {
            return null;
        }

        List<LotSimExport> sims = new();
        foreach (SimSnapshot sim in clientSnapshot.Sims)
        {
            if (!ownedSims.Contains(sim.ObjectId))
            {
                continue;
            }

            // Owned Sims off the active lot are outside the lot boundary this slice reconciles.
            if (sim.LotId is ulong simLot && simLot != exportLot)
            {
                continue;
            }

            sims.Add(new LotSimExport(sim.ObjectId, sim.Position, sim.CurrentInteractionKey));
        }

        if (sims.Count == 0)
        {
            return null;
        }

        return new LotExportBundle(playerId, exportLot, clientSnapshot.Tick, sims);
    }
}
