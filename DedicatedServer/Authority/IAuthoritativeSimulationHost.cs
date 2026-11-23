using SimsOnline.DedicatedServer.Protocol;
using SimsOnline.DedicatedServer.Mirroring;

namespace SimsOnline.DedicatedServer.Authority;

public interface IAuthoritativeSimulationHost : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task LoadSaveAsync(string saveName, CancellationToken cancellationToken);

    /// <summary>
    /// P2.5 load-path comparison probe: load via the menu API
    /// (Responder.Instance.MainMenuModel.LoadAndTransitionToSaveGame) rather than the direct
    /// GameStates.TransitionToGameStateToInWorld(..., force: true) path used by LoadSaveAsync.
    /// Both paths must reach SESSION_READY on success.
    /// </summary>
    Task LoadSaveViaMenuAsync(string saveName, CancellationToken cancellationToken);

    Task SaveGameAsync(string saveName, CancellationToken cancellationToken);

    /// <summary>
    /// slice 6: apply a validated client-native owned-lot class A bundle into the
    /// server TS3 (MERGE_LOT_STATE bridge command) so the next SAVE_GAME checkpoint persists the
    /// client's live lot state. The bundle must already be lot-scoped and ownership-filtered by
    /// the session (LotExportValidator) — the host is a dumb transport for the merge.
    /// </summary>
    Task MergeLotStateAsync(LotExportBundle bundle, CancellationToken cancellationToken);

    Task<HouseholdExportPayload?> ExportHouseholdContentAsync(
        ulong householdId,
        CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<HouseholdExportPayload?>(cancellationToken)
            : Task.FromResult<HouseholdExportPayload?>(null);

    Task<CommandResult> ApplyCommandAsync(ClientCommand command, CancellationToken cancellationToken);

    Task<SimulationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// P3.3 / P5.3: snapshot filtered to entities changed after <paramref name="sinceTick"/>.
    /// Pass -1 for a full snapshot (mod sentinel).
    /// </summary>
    Task<SimulationSnapshot> GetSnapshotSinceAsync(long sinceTick, CancellationToken cancellationToken);

    /// <summary>
    /// server-side autonomy suppression for claimed Sims. Claims previously only
    /// broadcast SimAutonomyChanged to client mirrors, so the authoritative TS3's claimed
    /// Sims kept running their own AI and player commands queued behind it. disabled=true
    /// also cancels the Sim's running interaction so the first player command takes effect
    /// promptly. Best-effort: callers must tolerate failures without failing the claim.
    /// claim no longer calls this — the command-lifecycle gate
    /// (RegisterAutonomyGateAsync/ClearAutonomyGateAsync) replaced the claim-time blanket disable.
    /// The primitive stays for probes and any future server-driven suppression.
    /// </summary>
    Task SetServerAutonomyAsync(ulong simObjectId, bool disabled, CancellationToken cancellationToken);

    /// <summary>
    /// register a claimed Sim for the command-lifecycle autonomy gate on the server-
    /// authority TS3. Does NOT disable autonomy — the Sim keeps free will until a player command
    /// (a UserDirected interaction) enters its queue, then the mod's pump suppresses autonomy for
    /// the duration of the command and resumes it when the directed queue drains. Best-effort:
    /// callers must tolerate failures without failing the claim (same contract as
    /// SetServerAutonomyAsync).
    /// </summary>
    Task RegisterAutonomyGateAsync(ulong simObjectId, CancellationToken cancellationToken);

    /// <summary>
    /// clear the command-lifecycle autonomy gate for a Sim on release/leave/disconnect.
    /// The mod unwinds any outstanding mid-command suppression so no autonomy disable leaks past
    /// the claim. Best-effort.
    /// </summary>
    Task ClearAutonomyGateAsync(ulong simObjectId, CancellationToken cancellationToken);

    /// <summary>
    /// reload-safe travel-merge. Default off ( quarantined).
    /// Imports into a fresh temp household, then merges into existing identities.
    /// Never calls ImportContent on the existing live household.
    /// </summary>
    Task<HouseholdImportResult> TravelMergeHouseholdAsync(
        HouseholdExportPayload payload,
        CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<HouseholdImportResult>(cancellationToken)
            : Task.FromResult(new HouseholdImportResult(
                Success: false,
                HouseholdId: payload?.HouseholdId ?? 0,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: "TravelMerge not implemented on this host type."));

    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// probe a Sim's motive value. Returns null when not supported.
    /// </summary>
    Task<float?> ProbeSimMotiveAsync(ulong simObjectId, string motiveName, CancellationToken cancellationToken) =>
        Task.FromResult<float?>(null);

    /// <summary>
    /// probe a household's FamilyFunds. Returns null when not supported.
    /// </summary>
    Task<int?> ProbeHouseholdFundsAsync(ulong householdId, CancellationToken cancellationToken) =>
        Task.FromResult<int?>(null);

    /// <summary>
    /// probe a Sim's skill level. Returns null when not supported.
    /// </summary>
    Task<int?> ProbeSimSkillAsync(ulong simObjectId, string skillName, CancellationToken cancellationToken) =>
        Task.FromResult<int?>(null);

    /// <summary>
    /// W1 (docs/server-authoritative-plan.md): publish the server TS3's simulation scope, i.e. the lots
    /// players occupy and the Sims they own, so the forked gameplay assembly simulates them like
    /// the active lot. Replaces the whole scope. Returns the mod's report (contains
    /// <c>hook=present</c> or <c>hook=absent</c>), or null when the host has no such channel.
    /// Best-effort: callers must tolerate failures.
    /// </summary>
    Task<string?> SetSimulationScopeAsync(SimulationScope scope, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// W2 part B: motives + buffs of the given Sims (missing Sims left out), in one bridge round trip
    /// per SimStateWire.MaxSimsPerProbe Sims. Empty when the host has no such channel.
    /// </summary>
    Task<IReadOnlyList<SimStateSample>> ProbeSimStateAsync(IReadOnlyList<ulong> simObjectIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SimStateSample>>(Array.Empty<SimStateSample>());

    /// <summary>
    /// W2 Sim identity step 3: the presence journal entries after <paramref name="sinceSeq"/> (Sims that
    /// entered or left the server's world). Null when the host has no such channel.
    /// </summary>
    Task<SimPresenceReport?> ProbeSimPresenceAsync(long sinceSeq, CancellationToken cancellationToken) =>
        Task.FromResult<SimPresenceReport?>(null);

    /// <summary>
    /// one Sim description exported by the server TS3 (EXPORT_SIM_DESCRIPTION), for a client that
    /// lacks it. Null when the host has no such channel or the description is gone.
    /// </summary>
    Task<SimDescriptionPayload?> ExportSimDescriptionAsync(ulong simDescriptionId, CancellationToken cancellationToken) =>
        Task.FromResult<SimDescriptionPayload?>(null);

    /// <summary>
    /// The world a joining client loads: checkpoint the server TS3's session to its own save (SAVE_GAME) and read
    /// that save folder back. Null when the host has no save on disk.
    /// </summary>
    Task<WorldSave?> CheckpointWorldAsync(CancellationToken cancellationToken) =>
        Task.FromResult<WorldSave?>(null);

    /// <summary> step 5: job, household funds and skills of these Sims (PROBE_SIM_DETAILS); missing Sims
    /// are left out. Empty when the host has no such channel.</summary>
    Task<IReadOnlyList<SimDetailsSample>> ProbeSimDetailsAsync(IReadOnlyList<ulong> simObjectIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SimDetailsSample>>(Array.Empty<SimDetailsSample>());

    /// <summary>
    /// presentation stream: one SACS_RECORDER call on the server TS3 (args are the verb's own
    /// fields). Returns the mod's report, or null when the host has no such channel.
    /// </summary>
    Task<string?> SacsRecorderAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
