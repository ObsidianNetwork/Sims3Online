using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Authority;

public sealed class HeadlessSims3Host : IAuthoritativeSimulationHost, IMirrorHost
{
    private const string DefaultSandboxieStartPath = @"C:\Program Files\Sandboxie-Plus\Start.exe";
    private static readonly TimeSpan SandboxieProcessDiscoveryTimeout = TimeSpan.FromSeconds(30);
    // Wine's loader takes noticeably longer than Sandboxie to expose the reparented TS3W.exe.
    private static readonly TimeSpan WineProcessDiscoveryTimeout = TimeSpan.FromSeconds(60);

    private readonly SessionConfig _config;
    private readonly HostBridgeServer _bridge;
    private Process? _hostProcess;
    private bool _ownsHostProcess;
    private bool _started;

    public HeadlessSims3Host(SessionConfig config)
    {
        _config = config;
        _bridge = new HostBridgeServer(config.HostBridgeEndpoint);
    }

    /// <summary>Exposes the underlying bridge for subscribers (probe modes, future client listener).</summary>
    public HostBridgeServer Bridge => _bridge;

    /// <summary>
    /// 2026-07-03 dev-tooling phase 1: lifecycle phase sink (RunStateFile.Transition). Lets the
    /// state file distinguish "scanning for the game" from "handshake done" without log greps.
    /// </summary>
    public Action<string, string?>? PhaseSink { get; set; }

    // adversarial durability testing flags. When set, the corresponding
    // bridge operation throws, simulating a live failure. Toggled via flag files:
    // .omo/run/dev204-inject-import-failure.flag (one-shot, consumed on next call)
    // .omo/run/dev204-inject-merge-failure.flag (one-shot)
    // .omo/run/dev204-inject-save-failure.flag (one-shot)
    // Also settable directly by tests via the public property.
    public bool ThrowOnNextImport { get; set; }
    public bool ThrowOnNextMergeLotState { get; set; }
    public bool ThrowOnNextSaveGame { get; set; }

    private static readonly string Dev204FlagDir = Path.Combine(".omo", "run");
    private static bool ConsumeDev204Flag(string name)
    {
        try
        {
            string path = Path.Combine(Dev204FlagDir, name);
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>The Sandboxie box the TS3 runs in (null: not sandboxed). Its named objects live in the box's
    /// namespace (SimLink sidecar sections).</summary>
    public string? SandboxieBoxName => string.IsNullOrEmpty(_config.Ts3SandboxieBoxName) ? null : _config.Ts3SandboxieBoxName;

    /// <summary>Live TS3 process id once StartAsync has attached/launched (state-file tooling).</summary>
    public int? HostProcessId
    {
        get
        {
            try
            {
                return _hostProcess?.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
    }

    /// <summary>The physical fork census StartAsync runs before it launches or attaches TS3 (tests supply a fake).</summary>
    public Func<ForkCensusScope, ForkCensusResult> ForkCensus { get; set; } = ForkCensusGuard.RunScript;

    /// <summary>Where an attached TS3 runs (its Sandboxie box or Wine prefix), so the census covers its profile.</summary>
    public Func<Process, AttachedProfile> AttachedProfileOf { get; set; } = ForkCensusGuard.ResolveAttachedProfile;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The same fail-closed census the Python launchers run, so a controller started or attached directly
        // cannot drive a stale or missing gameplay fork. Attach first finds its target (starting nothing), so the
        // census covers the profile that TS3 actually runs in.
        Process? attached = _config.AttachExistingHost ? AttachToExistingHost() : null;
        try
        {
            ForkCensusGuard.Enforce(ForkCensus,
                ForkCensusScope.For(_config, attached == null ? null : AttachedProfileOf(attached)), Console.WriteLine);
        }
        catch
        {
            attached?.Dispose();
            throw;
        }

        // attach mode targets an already-running process; the executable path is only
        // needed when we launch TS3 ourselves.
        if (!_config.AttachExistingHost && !File.Exists(ResolveHostExecutablePath(_config.Ts3ExecutablePath, _config.ClientMirrorWineprefixPath)))
        {
            throw new FileNotFoundException("Could not find TS3W.exe for the dedicated Sims 3 host.", _config.Ts3ExecutablePath);
        }

        try
        {
            await _bridge.StartAsync(cancellationToken);
            PhaseSink?.Invoke("host-starting", _config.AttachExistingHost ? "attach" : "launch");
            _hostProcess = attached ?? LaunchHostProcess(cancellationToken);
            PhaseSink?.Invoke("host-scanning", $"gamePid={_hostProcess.Id}");
            await _bridge.WaitForConnectionAsync(_config.HostBridgeTimeout, cancellationToken, _hostProcess.Id);
            if (!string.IsNullOrWhiteSpace(_bridge.ModBuildStamp))
            {
                Console.WriteLine($"Host mod build: {_bridge.ModBuildStamp}");
            }

            PhaseSink?.Invoke("host-connected", $"gamePid={_hostProcess.Id} mod={_bridge.ModBuildStamp}");
            _started = true;
        }
        catch
        {
            try
            {
                await StopOwnedHostProcessAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    public async Task LoadSaveAsync(string saveName, CancellationToken cancellationToken)
    {
        EnsureStarted();
        CheckBootSave(saveName);
        _reusableWorld = null; // a load replaces the world the last save held
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.LoadSave(Guid.NewGuid(), saveName), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "The Sims 3 host rejected the save load request.");
        }
    }

    public async Task LoadSaveViaMenuAsync(string saveName, CancellationToken cancellationToken)
    {
        EnsureStarted();
        _reusableWorld = null;
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.LoadSaveViaMenu(Guid.NewGuid(), saveName), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "The Sims 3 host rejected the menu-path save load request.");
        }
    }

    public async Task SaveGameAsync(string saveName, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            // A public save can change state outside the join fingerprint. Forget the old bytes; only a save to
            // the join checkpoint slot may seed reuse, so clients still receive that slot's folder name.
            _reusableWorld = null;
            bool checkpointSave = string.Equals(
                Path.GetFileNameWithoutExtension(saveName), CheckpointSaveId(), StringComparison.OrdinalIgnoreCase);
            bool fingerprint = checkpointSave && await ArmJoinFingerprintAsync(cancellationToken);
            Stopwatch watch = Stopwatch.StartNew();
            await SaveGameCoreAsync(saveName, cancellationToken);
            double savedMs = watch.Elapsed.TotalMilliseconds;
            string? fingerprintReport = fingerprint ? await TrySacsRecorderAsync(cancellationToken, "fingerprint-saved") : null;
            string? folder = FindNewestSaveFolder(Path.GetFileNameWithoutExtension(saveName));
            if (folder != null)
            {
                WorldSave save = WorldSave.ReadFolder(folder, Path.GetFileName(folder));
                if (checkpointSave)
                {
                    JoinReuse.RecordSave(savedMs, joinCheckpoint: false);
                    KeepReusableWorld(save, fingerprintReport);
                }

                // Awaited: a public save often comes right before a shutdown (persist_cycle), and that copy matters most.
                await KeepLastGoodAsync(folder, save);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveGameCoreAsync(string saveName, CancellationToken cancellationToken)
    {
        EnsureStarted();
        ThrowIfSaveFailureInjected();
        // CurrentSaveIdFor only preserves the loaded slot's extension; save-as names must carry their own.
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SaveGame(Guid.NewGuid(), EnsureSims3Extension(saveName)), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "The Sims 3 host rejected the save request.");
        }
    }

    /// <summary>
    /// slice 6: lot-boundary reconcile write. Oversized bundles are pre-chunked to the
    /// single-frame budget; each chunk is one MERGE_LOT_STATE round-trip (mirrors ApplyPositionsAsync).
    /// </summary>
    public async Task MergeLotStateAsync(LotExportBundle bundle, CancellationToken cancellationToken)
    {
        EnsureStarted();
        // adversarial failure injection.
        if (ThrowOnNextMergeLotState || ConsumeDev204Flag("dev204-inject-merge-failure.flag"))
        {
            ThrowOnNextMergeLotState = false;
            throw new InvalidOperationException("DEV-204 injected MERGE_LOT_STATE failure.");
        }
        foreach (BridgeCommand.MergeLotState chunk in BridgeLineCodec.ChunkMergeLotState(bundle))
        {
            BridgeResponse response = await SendBridgeCommandAsync(chunk, cancellationToken);
            if (!response.IsAccepted)
            {
                throw new InvalidOperationException($"MERGE_LOT_STATE rejected: {response.RejectionReason}");
            }
        }
    }

    /// <summary>
    /// diagnostic transport for the now-quarantined live household import command.
    /// prohibits production callers until an engine-supported lifecycle passes cold reload.
    /// </summary>
    internal async Task<HouseholdImportResult> ImportHouseholdContentAsync(
        HouseholdExportPayload payload,
        CancellationToken cancellationToken)
    {
        EnsureStarted();

        // adversarial failure injection.
        if (ThrowOnNextImport || ConsumeDev204Flag("dev204-inject-import-failure.flag"))
        {
            ThrowOnNextImport = false;
            throw new InvalidOperationException("DEV-204 injected IMPORT_HOUSEHOLD_CONTENT failure.");
        }

        HouseholdExportRejectionReason validation = HouseholdExportValidator.Validate(payload);
        if (validation != HouseholdExportRejectionReason.Accepted)
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload?.HouseholdId ?? 0,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: $"deep export payload rejected: {validation}");
        }

        // Upload the deep bytes via the existing 512 KiB BULK slot path (same path probe uploads use).
        string blobId = $"dev201-import-{payload.HouseholdId}";
        byte[] combinedBytes = CombineExportBytes(payload!);
        BridgeResponse uploadResponse = await _bridge.UploadInboundBlobAsync(blobId, combinedBytes, cancellationToken);
        if (!uploadResponse.IsAccepted)
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload.HouseholdId,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: $"BULK upload rejected: {uploadResponse.RejectionReason}");
        }

        // Send the control frame — the mod fetches the blob and runs ImportContent.
        uint crc = ComputeCrc32(combinedBytes);
        var importCommand = new BridgeCommand.ImportHouseholdContent(
            Guid.NewGuid(),
            payload.HouseholdId,
            combinedBytes.Length,
            payload.PropertyStreamBytes.Length,
            payload.ResKeyTableBytes.Length,
            payload.ObjectIdTableBytes.Length,
            crc,
            payload.EntryCount,
            payload.ResKeyCount,
            payload.ObjectIdCount);

        BridgeResponse response = await SendBridgeCommandAsync(importCommand, cancellationToken);
        if (!response.IsAccepted)
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload.HouseholdId,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: response.RejectionReason ?? "IMPORT_HOUSEHOLD_CONTENT rejected");
        }

        // Parse the import report from the probe-report channel (carried in RejectionReason field).
        var remapped = ParseRemappedSimDescriptionIds(response.ProbeReport);
        return new HouseholdImportResult(
            Success: true,
            HouseholdId: payload.HouseholdId,
            EntryCount: payload.EntryCount,
            RemappedSimDescriptionIds: remapped,
            FailureReason: null);
    }

    /// <summary>
    /// reload-safe travel-merge. Flag-gated off in production.
    /// Uploads BULK blob, sends MERGE_TRAVELLING_HOUSEHOLD control frame, parses report.
    /// </summary>
    public async Task<HouseholdImportResult> TravelMergeHouseholdAsync(
        HouseholdExportPayload payload,
        CancellationToken cancellationToken)
    {
        EnsureStarted();

        // flag-gated. Production default = off (quarantined).
        if (!ConsumeDev204Flag("dev205-enable-travel-merge.flag"))
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload.HouseholdId,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: "TravelMerge quarantined: dev205-enable-travel-merge.flag not present.");
        }

        HouseholdExportRejectionReason validation = HouseholdExportValidator.Validate(payload);
        if (validation != HouseholdExportRejectionReason.Accepted)
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload.HouseholdId,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: $"deep export payload rejected: {validation}");
        }

        // Upload BULK blob (same path as ImportHouseholdContentAsync).
        string blobId = $"dev205-travelmerge-{payload.HouseholdId}";
        byte[] combinedBytes = CombineExportBytes(payload);
        BridgeResponse uploadResponse = await _bridge.UploadInboundBlobAsync(blobId, combinedBytes, cancellationToken);
        if (!uploadResponse.IsAccepted)
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload.HouseholdId,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: $"BULK upload rejected: {uploadResponse.RejectionReason}");
        }

        // Send control frame.
        uint crc = ComputeCrc32(combinedBytes);
        int inventoryCount = (payload.SimInventories?.Count ?? 0)
            + (payload.SharedFridgeInventory != null ? 1 : 0)
            + (payload.SharedFamilyInventory != null ? 1 : 0);
        var mergeCommand = new BridgeCommand.MergeTravellingHousehold(
            Guid.NewGuid(),
            payload.HouseholdId,
            combinedBytes.Length,
            crc,
            payload.PropertyStreamBytes.Length,
            payload.ResKeyTableBytes.Length,
            payload.ObjectIdTableBytes.Length,
            inventoryCount);

        BridgeResponse response = await SendBridgeCommandAsync(mergeCommand, cancellationToken);
        if (!response.IsAccepted)
        {
            return new HouseholdImportResult(
                Success: false,
                HouseholdId: payload.HouseholdId,
                EntryCount: 0,
                RemappedSimDescriptionIds: Array.Empty<(ulong, ulong)>(),
                FailureReason: response.RejectionReason ?? "MERGE_TRAVELLING_HOUSEHOLD rejected");
        }

        var remapped = ParseRemappedSimDescriptionIds(response.ProbeReport);
        return new HouseholdImportResult(
            Success: true,
            HouseholdId: payload.HouseholdId,
            EntryCount: payload.EntryCount,
            RemappedSimDescriptionIds: remapped,
            FailureReason: null);
    }

    public async Task<HouseholdExportPayload?> ExportHouseholdContentAsync(
        ulong householdId,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();

        Guid correlationId = Guid.NewGuid();
        BridgeResponse response = await SendBridgeCommandAsync(
            new BridgeCommand.ExportHouseholdContent(correlationId, householdId),
            cancellationToken);
        if (!response.IsAccepted)
        {
            return null;
        }

        HouseholdExportMetadataDecodeResult metadataResult =
            BridgeLineCodec.DecodeHouseholdExportMetadata(response.ProbeReport);
        if (!metadataResult.IsSuccess)
        {
            throw new HouseholdExportTransportException(
                metadataResult.Error,
                metadataResult.Detail ?? "Household export metadata could not be decoded.");
        }

        HouseholdExportMetadata metadata = metadataResult.Metadata!;
        cancellationToken.ThrowIfCancellationRequested();
        HostBulkReadResult bulkResult = _bridge.ReadHostBulkPayload(
            correlationId,
            metadata.SlotIndex,
            metadata.TotalBytes,
            metadata.Crc32);
        cancellationToken.ThrowIfCancellationRequested();
        if (!bulkResult.IsSuccess)
        {
            throw new HouseholdExportTransportException(
                bulkResult.Error,
                bulkResult.Detail ?? "Household export BULK payload could not be read.");
        }

        HouseholdExportPayloadDecodeResult payloadResult = HouseholdExportTransport.DecodePayload(
            householdId,
            metadata,
            bulkResult.Payload!);
        if (payloadResult.Error == HouseholdExportTransportError.ValidatorRejected)
        {
            return null;
        }

        if (!payloadResult.IsSuccess)
        {
            throw new HouseholdExportTransportException(
                payloadResult.Error,
                payloadResult.Detail ?? "Household export payload could not be decoded.");
        }

        return payloadResult.Payload;
    }

    private static byte[] CombineExportBytes(HouseholdExportPayload payload)
    {
        // combined blob = household triplet + each inventory triplet.
        int totalLen = payload.PropertyStreamBytes.Length
            + payload.ResKeyTableBytes.Length
            + payload.ObjectIdTableBytes.Length;

        if (payload.SimInventories != null)
        {
            foreach (InventoryExportPayload inv in payload.SimInventories)
            {
                totalLen += inv.PropertyStreamBytes.Length + inv.ResKeyTableBytes.Length + inv.ObjectIdTableBytes.Length;
            }
        }

        if (payload.SharedFridgeInventory != null)
        {
            totalLen += payload.SharedFridgeInventory.PropertyStreamBytes.Length
                + payload.SharedFridgeInventory.ResKeyTableBytes.Length
                + payload.SharedFridgeInventory.ObjectIdTableBytes.Length;
        }

        if (payload.SharedFamilyInventory != null)
        {
            totalLen += payload.SharedFamilyInventory.PropertyStreamBytes.Length
                + payload.SharedFamilyInventory.ResKeyTableBytes.Length
                + payload.SharedFamilyInventory.ObjectIdTableBytes.Length;
        }

        byte[] combined = new byte[totalLen];
        int offset = 0;

        // Household triplet.
        Buffer.BlockCopy(payload.PropertyStreamBytes, 0, combined, offset, payload.PropertyStreamBytes.Length);
        offset += payload.PropertyStreamBytes.Length;
        Buffer.BlockCopy(payload.ResKeyTableBytes, 0, combined, offset, payload.ResKeyTableBytes.Length);
        offset += payload.ResKeyTableBytes.Length;
        Buffer.BlockCopy(payload.ObjectIdTableBytes, 0, combined, offset, payload.ObjectIdTableBytes.Length);
        offset += payload.ObjectIdTableBytes.Length;

        // Inventory triplets.
        if (payload.SimInventories != null)
        {
            foreach (InventoryExportPayload inv in payload.SimInventories)
            {
                Buffer.BlockCopy(inv.PropertyStreamBytes, 0, combined, offset, inv.PropertyStreamBytes.Length);
                offset += inv.PropertyStreamBytes.Length;
                Buffer.BlockCopy(inv.ResKeyTableBytes, 0, combined, offset, inv.ResKeyTableBytes.Length);
                offset += inv.ResKeyTableBytes.Length;
                Buffer.BlockCopy(inv.ObjectIdTableBytes, 0, combined, offset, inv.ObjectIdTableBytes.Length);
                offset += inv.ObjectIdTableBytes.Length;
            }
        }

        if (payload.SharedFridgeInventory != null)
        {
            Buffer.BlockCopy(payload.SharedFridgeInventory.PropertyStreamBytes, 0, combined, offset, payload.SharedFridgeInventory.PropertyStreamBytes.Length);
            offset += payload.SharedFridgeInventory.PropertyStreamBytes.Length;
            Buffer.BlockCopy(payload.SharedFridgeInventory.ResKeyTableBytes, 0, combined, offset, payload.SharedFridgeInventory.ResKeyTableBytes.Length);
            offset += payload.SharedFridgeInventory.ResKeyTableBytes.Length;
            Buffer.BlockCopy(payload.SharedFridgeInventory.ObjectIdTableBytes, 0, combined, offset, payload.SharedFridgeInventory.ObjectIdTableBytes.Length);
            offset += payload.SharedFridgeInventory.ObjectIdTableBytes.Length;
        }

        if (payload.SharedFamilyInventory != null)
        {
            Buffer.BlockCopy(payload.SharedFamilyInventory.PropertyStreamBytes, 0, combined, offset, payload.SharedFamilyInventory.PropertyStreamBytes.Length);
            offset += payload.SharedFamilyInventory.PropertyStreamBytes.Length;
            Buffer.BlockCopy(payload.SharedFamilyInventory.ResKeyTableBytes, 0, combined, offset, payload.SharedFamilyInventory.ResKeyTableBytes.Length);
            offset += payload.SharedFamilyInventory.ResKeyTableBytes.Length;
            Buffer.BlockCopy(payload.SharedFamilyInventory.ObjectIdTableBytes, 0, combined, offset, payload.SharedFamilyInventory.ObjectIdTableBytes.Length);
            offset += payload.SharedFamilyInventory.ObjectIdTableBytes.Length;
        }

        return combined;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        // CRC-32 (IEEE 802.3 polynomial, same as the bridge BULK checksum).
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(crc & 1));
            }
        }
        return ~crc;
    }

    private static IReadOnlyList<(ulong OriginalId, ulong RemappedId)> ParseRemappedSimDescriptionIds(string? report)
    {
        if (string.IsNullOrEmpty(report))
        {
            return Array.Empty<(ulong, ulong)>();
        }

        // The mod reports JSON: {"remappedIds": [["origDec","newDec"], ...], "entryCount": N}
        // For the controller in-memory host / test harness, this is a best-effort parse.
        // Production parsing uses System.Text.Json; this stays simple for the .NET 8 controller.
        try
        {
            using JsonDocument doc = JsonDocument.Parse(report);
            List<(ulong, ulong)> result = new();
            if (doc.RootElement.TryGetProperty("remappedIds", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement pair in ids.EnumerateArray())
                {
                    if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2)
                    {
                        ulong orig = pair[0].GetUInt64();
                        ulong remap = pair[1].GetUInt64();
                        result.Add((orig, remap));
                    }
                }
            }
            return result;
        }
        catch
        {
            return Array.Empty<(ulong, ulong)>();
        }
    }

    public async Task<CommandResult> ApplyCommandAsync(ClientCommand command, CancellationToken cancellationToken)
    {
        EnsureStarted();

        BridgeCommand? bridgeCommand = command switch
        {
            ClientCommand.SetGameSpeed setGameSpeed => new BridgeCommand.SetGameSpeed(command.CommandId, setGameSpeed.Speed),
            ClientCommand.SetWorldTime setWorldTime => new BridgeCommand.SetWorldTime(command.CommandId, setWorldTime.Time),
            ClientCommand.QueueInteraction queueInteraction => new BridgeCommand.QueueInteraction(command.CommandId, queueInteraction.Interaction),
            ClientCommand.RequestSnapshot reqSnapshot => new BridgeCommand.RequestSnapshot(command.CommandId, reqSnapshot.SinceTick),
            ClientCommand.SetActiveSim setActiveSim => new BridgeCommand.SetActiveSim(command.CommandId, setActiveSim.SimObjectId),
            ClientCommand.CancelInteraction cancelInteraction => new BridgeCommand.CancelInteraction(command.CommandId, cancelInteraction.ActorObjectId, cancelInteraction.InteractionId),
            ClientCommand.LeaveConversation leaveConversation => new BridgeCommand.LeaveConversation(command.CommandId, leaveConversation.ActorObjectId),
            ClientCommand.AnswerDialog answerDialog => new BridgeCommand.AnswerDialog(command.CommandId, answerDialog.RequestId, answerDialog.Answer),
            _ => null
        };

        if (bridgeCommand == null)
        {
            return CommandResult.Rejected(command.CommandId, $"Command {command.GetType().Name} is not implemented by the Sims 3 host bridge yet.");
        }

        BridgeResponse response = await SendBridgeCommandAsync(bridgeCommand, cancellationToken);
        if (!response.IsAccepted)
        {
            return CommandResult.Rejected(command.CommandId, response.RejectionReason ?? "Rejected by Sims 3 host.");
        }

        List<ServerEvent> events = new();
        if (command is ClientCommand.SetGameSpeed setSpeed)
        {
            events.Add(new ServerEvent.GameSpeedChanged(setSpeed.Speed));
        }

        if (command is ClientCommand.SetWorldTime worldTimeCmd)
        {
            events.Add(new ServerEvent.WorldTimeChanged(worldTimeCmd.Time));
        }

        if (response.Snapshot != null)
        {
            if (command is ClientCommand.RequestSnapshot reqSnapshot && reqSnapshot.SinceTick.HasValue)
            {
                events.Add(new ServerEvent.SnapshotDelta(response.Snapshot));
            }
            else
            {
                events.Add(new ServerEvent.FullSnapshot(response.Snapshot));
            }
        }

        return CommandResult.Accepted(command.CommandId, events.ToArray());
    }

    public Task<SimulationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        return GetSnapshotSinceAsync(-1L, cancellationToken);
    }

    public async Task<SimulationSnapshot> GetSnapshotSinceAsync(long sinceTick, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(
            new BridgeCommand.RequestSnapshot(Guid.NewGuid(), sinceTick),
            cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "The Sims 3 host rejected the snapshot request.");
        }

        return response.Snapshot ?? throw new InvalidOperationException("The Sims 3 host accepted the snapshot request without returning a snapshot.");
    }

    /// <summary>
    /// server-side autonomy suppression for claimed Sims (see IAuthoritativeSimulationHost).
    /// disabled=true sends DISABLE_AUTONOMY; disabled=false sends ENABLE_AUTONOMY (counter-based
    /// on the mod side — callers must pair calls, which AuthoritativeSession's idempotent claim
    /// map does).
    /// cancel-running is deliberately NOT sent. Claim-time CancelAllInteractions killed
    /// boot-time socials without popping the Sim's SocializingPosture, and the orphaned posture
    /// made every subsequent player command fail its posture transition at start (~15ms,
    /// empty-key "Interaction callback failed" — the claimed-Sim freeze).'s promptness
    /// goal is met natively: the mod queues GoHere via AddNext at UserDirected priority, which
    /// preempts a running autonomous interaction posture-safely (live-proven 2026-06-12:
    /// HigherPriorityNext cancelled a running ReadBook and the Sim walked).
    /// </summary>
    public async Task SetServerAutonomyAsync(ulong simObjectId, bool disabled, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeCommand command = disabled
            ? new BridgeCommand.DisableAutonomy(Guid.NewGuid(), simObjectId)
            : new BridgeCommand.EnableAutonomy(Guid.NewGuid(), simObjectId);
        BridgeResponse response = await SendBridgeCommandAsync(command, cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"{(disabled ? "DISABLE" : "ENABLE")}_AUTONOMY rejected: {response.RejectionReason}");
        }
    }

    /// <summary>
    /// register a claimed Sim for the command-lifecycle autonomy gate (see
    /// IAuthoritativeSimulationHost). Sends GATE_AUTONOMY; the mod tracks the Sim and only
    /// suppresses autonomy while a player command is in its queue. Unlike the claim-time
    /// DISABLE_AUTONOMY this does not disable autonomy on registration.
    /// </summary>
    public async Task RegisterAutonomyGateAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        lock (_claimedThisRun)
        {
            // a claim counts for the join fingerprint at once, not a broadcaster tick later (the published
            // simulation scope adds the same Sims); never removed when the gate clears.
            _claimedThisRun.Add(simObjectId);
        }

        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.GateAutonomy(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"GATE_AUTONOMY rejected: {response.RejectionReason}");
        }
    }

    /// <summary>
    /// clear the command-lifecycle autonomy gate for a Sim (see
    /// IAuthoritativeSimulationHost). Sends UNGATE_AUTONOMY; the mod untracks the Sim and unwinds
    /// any outstanding mid-command suppression.
    /// </summary>
    public async Task ClearAutonomyGateAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.UngateAutonomy(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"UNGATE_AUTONOMY rejected: {response.RejectionReason}");
        }
    }

    /// <summary>
    /// W1: send SET_SIM_SCOPE (see IAuthoritativeSimulationHost.SetSimulationScopeAsync). The mod
    /// drops the world lot and unknown lots, applies the rest to the forked SimScopeHook, and
    /// reports hook=present|absent.
    /// </summary>
    public async Task<string?> SetSimulationScopeAsync(SimulationScope scope, CancellationToken cancellationToken)
    {
        EnsureStarted();
        lock (_claimedThisRun)
        {
            _claimedThisRun.UnionWith(scope.SimObjectIds); // the join fingerprint's Sims (ClaimedThisRun)
        }

        BridgeResponse response = await SendBridgeCommandAsync(
            new BridgeCommand.SetSimScope(Guid.NewGuid(), scope.LotIds, scope.SimObjectIds), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_SIM_SCOPE rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>
    /// W2: send SET_AUTHORITY (see IMirrorHost.SetAuthorityAsync). The mod sets the forked
    /// AuthorityHook mode and native Sims, or answers hook=absent on stock gameplay.
    /// </summary>
    public async Task<string?> SetAuthorityAsync(
        AuthorityGateMode mode,
        IReadOnlyList<ulong> nativeSimObjectIds,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(
            new BridgeCommand.SetAuthority(Guid.NewGuid(), mode, nativeSimObjectIds), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_AUTHORITY rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>W2 part B: PROBE_SIM_STATE in batches of SimStateWire.MaxSimsPerProbe.</summary>
    public async Task<IReadOnlyList<SimStateSample>> ProbeSimStateAsync(IReadOnlyList<ulong> simObjectIds, CancellationToken cancellationToken)
    {
        EnsureStarted();
        List<SimStateSample> samples = new();
        int batch = SimsOnlineServerHost.SimStateWire.MaxSimsPerProbe;
        for (int i = 0; i < simObjectIds.Count; i += batch)
        {
            List<ulong> ids = simObjectIds.Skip(i).Take(batch).ToList();
            BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimState(Guid.NewGuid(), ids), cancellationToken);
            if (!response.IsAccepted)
            {
                throw new InvalidOperationException($"PROBE_SIM_STATE rejected: {response.RejectionReason}");
            }

            samples.AddRange(SimStateSample.ParseProbeReport(response.ProbeReport));
        }

        return samples;
    }

    /// <summary>SACS_RECORDER (server role; see IAuthoritativeSimulationHost.SacsRecorderAsync).</summary>
    public async Task<string?> SacsRecorderAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SacsRecorder(Guid.NewGuid(), args), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SACS_RECORDER rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? string.Empty;
    }

    /// <summary>SACS_REPLAY (client role; see IMirrorHost.SacsReplayAsync).</summary>
    public async Task<string?> SacsReplayAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SacsReplay(Guid.NewGuid(), lines), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SACS_REPLAY rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>W2 Sim identity step 3: PROBE_SIM_PRESENCE (server role).</summary>
    public async Task<SimPresenceReport?> ProbeSimPresenceAsync(long sinceSeq, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimPresence(Guid.NewGuid(), sinceSeq), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_SIM_PRESENCE rejected: {response.RejectionReason}");
        }

        return SimPresenceReport.Parse(response.ProbeReport)
            ?? throw new InvalidOperationException($"PROBE_SIM_PRESENCE report malformed: {response.ProbeReport}");
    }

    /// <summary>W2 Sim identity step 3: SPAWN_SIM (client role).</summary>
    public async Task<string?> SpawnSimAsync(ulong simObjectId, ulong simDescriptionId, Vector3Dto position, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SpawnSim(Guid.NewGuid(), simObjectId, simDescriptionId, position), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SPAWN_SIM rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>W2 Sim identity step 3: DESPAWN_SIM (client role).</summary>
    public async Task<string?> DespawnSimAsync(ulong simObjectId, ulong simDescriptionId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.DespawnSim(Guid.NewGuid(), simObjectId, simDescriptionId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"DESPAWN_SIM rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>
    /// SAVE_GAME under the save the server loaded, then read the save it just wrote for a joining client. TS3 writes
    /// SAVE_GAME "Name" into the folder "Name" (the loaded save is "Name.sims3"), so the newest of the two is read
    /// and shipped as "Name.sims3", the folder a client loads. Folders are looked for in
    /// SIMSONLINE_SERVER_SAVES_DIR, else in the TS3 Documents folders (the profile's and the shell's, which OneDrive
    /// may redirect).
    /// </summary>
    public async Task<WorldSave?> CheckpointWorldAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        // One save at a time (public saves too), so joins that arrive together share one save: the second one scans
        // after the first one's save and reuses it when nothing changed in between.
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            // An injected save failure fails this join whether or not it would reuse (C2 4a), and is never left
            // armed for a later save.
            ThrowIfSaveFailureInjected();
            if (!JoinReuseEnabled)
            {
                return await CheckpointFreshAsync(cancellationToken);
            }

            (JoinWorldReuse.Decision decision, string scan, ReusableWorld? reusable) = await DecideJoinReuseAsync(cancellationToken);
            if (decision.Reuse && reusable != null)
            {
                // Guard A: nothing was written, so the last good copy stays the last save's; no keep here.
                Console.WriteLine(JoinReuseLine(decision, scan + "; the last save served"));
                return reusable.World;
            }

            string outcome = "; save failed";
            try
            {
                WorldSave? world = await CheckpointFreshAsync(cancellationToken);
                outcome = "; saved fresh";
                return world;
            }
            finally
            {
                Console.WriteLine(JoinReuseLine(decision, scan + outcome));
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private string CheckpointSaveId() => Path.GetFileNameWithoutExtension(
        !string.IsNullOrWhiteSpace(_config.CheckpointSaveName) ? _config.CheckpointSaveName : _config.SaveName);

    // the join checkpoint's own save, as before "reuse if unchanged", with the join fingerprint taken in the same
    // SAVE_GAME (ArmJoinFingerprintAsync) and the world kept for the next join (KeepReusableWorld). Under _saveGate.
    private async Task<WorldSave?> CheckpointFreshAsync(CancellationToken cancellationToken)
    {
        string saveName = CheckpointSaveId();
        bool fingerprint = await ArmJoinFingerprintAsync(cancellationToken);
        DateTime started = DateTime.UtcNow;
        int refusals = 0;
        while (true)
        {
            try
            {
                await SaveGameCoreAsync(saveName, cancellationToken);
                break;
            }
            catch (InvalidOperationException ex) when (IsNotReadyToSave(ex.Message) && DateTime.UtcNow - started < CheckpointReadyWait)
            {
                // Right after its load the server game is not in-world yet, or cannot save yet: two boots in a row
                // failed on a joiner's world request (2026-10-21, mod idle poll 50 ms). It can, a moment later.
                refusals++;
                Console.WriteLine($"[world] checkpoint waits for the host to be able to save: {ex.Message}");
                await Task.Delay(CheckpointRetryDelay, cancellationToken);
            }
        }

        // One line per checkpoint: how long a joiner's world request held the save ('s stall) and how many "not
        // yet" refusals it waited through ('s retry-class refusals, capped by CheckpointReadyWait).
        double savedMs = (DateTime.UtcNow - started).TotalMilliseconds;
        JoinReuse.RecordSave(savedMs, joinCheckpoint: true);
        Console.WriteLine($"[world] checkpoint saved in {savedMs:F0} ms after {refusals} not-ready refusal(s).");
        string? fingerprintReport = fingerprint ? await TrySacsRecorderAsync(cancellationToken, "fingerprint-saved") : null;

        string? folder = FindNewestSaveFolder(saveName);
        if (folder == null)
        {
            return null;
        }

        WorldSave world = WorldSave.ReadFolder(folder, saveName + ".sims3");
        KeepReusableWorld(world, fingerprintReport);
        // Not awaited: the joiner should not wait for the copy. It is written from these bytes, so a later save cannot
        // tear it, and one cut short by a shutdown never replaces the previous copy (LastGoodSave.Keep's staging).
        _ = KeepLastGoodAsync(folder, world);
        return world;
    }

    /// <summary>a world a join may serve instead of saving, with the fingerprint taken in the save that wrote
    /// it. One reference, so a reader never pairs one save's bytes with another save's fingerprint.</summary>
    private sealed record ReusableWorld(WorldSave World, WorldFingerprint Fingerprint);

    // "reuse if unchanged" (JoinWorldReuse). SIMSONLINE_JOIN_REUSE=0 turns it off: every join saves fresh and
    // no fingerprint is taken, as before.
    private static bool JoinReuseEnabled => Environment.GetEnvironmentVariable("SIMSONLINE_JOIN_REUSE") != "0";
    private static readonly TimeSpan JoinScanTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    // Every Sim claimed during this run (the published simulation scopes' Sims, never removed): the households the
    // fingerprint covers stay covered after a player leaves, so the next join of that player is compared on them too.
    private readonly SortedSet<ulong> _claimedThisRun = new();
    private volatile ReusableWorld? _reusableWorld;

    /// <summary>the join checkpoints' counters (hits, misses by reason, scan and save ms, saves skipped,
    /// seconds saved), printed on each "[world] join reuse" line; scripts/join_fingerprint.py counters reads them.</summary>
    public JoinReuseCounters JoinReuse { get; } = new();

    // adversarial failure injection (ThrowOnNextSaveGame, or .omo/run/dev204-inject-save-failure.flag).
    private void ThrowIfSaveFailureInjected()
    {
        if (ThrowOnNextSaveGame || ConsumeDev204Flag("dev204-inject-save-failure.flag"))
        {
            ThrowOnNextSaveGame = false;
            throw new InvalidOperationException("DEV-204 injected SAVE_GAME failure.");
        }
    }

    private ulong[] ClaimedThisRun()
    {
        lock (_claimedThisRun)
        {
            return _claimedThisRun.ToArray();
        }
    }

    private string JoinReuseLine(JoinWorldReuse.Decision decision, string note) =>
        $"[world] join reuse: {(decision.Reuse ? "HIT" : "MISS (" + string.Join(", ", decision.Misses) + ")")}: {decision.Detail}; {note} | {JoinReuse.Format()}";

    /// <summary>scan the world at a join (SACS_RECORDER fingerprint-now, at most JoinScanTimeout) when the last
    /// save has a fingerprint, decide, and count it. Returns the decision, what the scan cost, and the world it was
    /// decided on.</summary>
    private async Task<(JoinWorldReuse.Decision Decision, string Scan, ReusableWorld? Reusable)> DecideJoinReuseAsync(CancellationToken cancellationToken)
    {
        ReusableWorld? reusable = _reusableWorld;
        WorldFingerprint? atJoin = null;
        string? error = null;
        string scan = "no scan";
        if (reusable != null)
        {
            Stopwatch watch = Stopwatch.StartNew();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(JoinScanTimeout);
            try
            {
                string? report = await SacsRecorderAsync(
                    ["fingerprint-now", SimsOnlineServerHost.SimScopeWire.Format(ClaimedThisRun())], timeout.Token);
                atJoin = WorldFingerprint.Parse(report, out error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = $"no answer in {JoinScanTimeout.TotalSeconds:F0} s";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }

            if (atJoin != null)
            {
                JoinReuse.RecordScan(atJoin.ScanMs);
            }

            scan = atJoin != null
                ? $"scan {atJoin.ScanMs:F1} ms in the game, {watch.ElapsedMilliseconds} ms in all"
                : $"scan failed after {watch.ElapsedMilliseconds} ms";
        }

        JoinWorldReuse.Decision decision = JoinWorldReuse.Decide(reusable?.Fingerprint, atJoin, error);
        JoinReuse.Record(decision);
        return (decision, scan, reusable);
    }

    /// <summary>tell the mod which claimed Sims the next save's join fingerprint covers. False when reuse is
    /// off or the mod did not take it (an older mod): that save is then not reusable.</summary>
    private async Task<bool> ArmJoinFingerprintAsync(CancellationToken cancellationToken)
    {
        if (!JoinReuseEnabled)
        {
            return false;
        }

        string? reply = await TrySacsRecorderAsync(
            cancellationToken, "fingerprint-scope", SimsOnlineServerHost.SimScopeWire.Format(ClaimedThisRun()));
        return reply != null && reply.StartsWith("fingerprint-scope=", StringComparison.Ordinal);
    }

    /// <summary>after a save of the server's own save, keep it for the next join with the fingerprint the mod
    /// took in that save. Only a save that is whole (LastGoodSave.Problem, Guard A's check) and has a fingerprint is
    /// kept; otherwise the next join saves fresh.</summary>
    private void KeepReusableWorld(WorldSave world, string? fingerprintReport)
    {
        if (!JoinReuseEnabled)
        {
            return;
        }

        WorldFingerprint? fingerprint = WorldFingerprint.Parse(fingerprintReport, out string? error);
        string? problem = fingerprint == null ? null : LastGoodSave.Problem(world.Files);
        if (fingerprint == null || problem != null)
        {
            _reusableWorld = null;
            Console.WriteLine($"[world] join reuse: this save is not kept for the next join ({(fingerprint == null ? "no fingerprint: " + error : "not whole: " + problem)}).");
            return;
        }

        JoinReuse.RecordScan(fingerprint.ScanMs);
        _reusableWorld = new ReusableWorld(world, fingerprint);
    }

    // A SACS_RECORDER call whose failure only costs the reuse (null), never the save; the caller's cancellation still
    // throws.
    private async Task<string?> TrySacsRecorderAsync(CancellationToken cancellationToken, params string[] args)
    {
        try
        {
            return await SacsRecorderAsync(args, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[world] join reuse: SACS_RECORDER {args[0]} failed: {ex.Message}");
            return null;
        }
    }

    private readonly SemaphoreSlim _lastGoodGate = new(1, 1);

    /// <summary>after a save that answered OK, keep what TS3 wrote (read right after the save) as the save's
    /// last good copy (LastGoodSave). Off the game's thread; one at a time; a failure is logged and never fails the
    /// save.</summary>
    private async Task KeepLastGoodAsync(string folder, WorldSave save)
    {
        await _lastGoodGate.WaitAsync();
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            string kept = await Task.Run(() => LastGoodSave.Keep(folder, save, DateTimeOffset.UtcNow));
            Console.WriteLine($"[save] last good copy kept: {Path.GetFileName(kept)} in {watch.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[save] LAST GOOD COPY NOT KEPT for {Path.GetFileName(folder)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _lastGoodGate.Release();
        }
    }

    /// <summary>before the server loads its save, restore the last good copy if the save is missing or not
    /// whole (LastGoodSave.EnsureBootable). Logged either way; a failure here never stops the load.</summary>
    private static void CheckBootSave(string saveName)
    {
        string folderName = saveName.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? saveName : saveName + ".sims3";
        string? folder = SaveRoots()
            .Select(root => Path.Combine(root, folderName))
            .FirstOrDefault(f => Directory.Exists(f) || Directory.Exists(LastGoodSave.FolderFor(f)));
        if (folder == null)
        {
            Console.WriteLine($"[save] boot save {folderName}: not found in the Saves folders, nor a last good copy.");
            return;
        }

        try
        {
            LastGoodSave.BootCheck check = LastGoodSave.EnsureBootable(folder, DateTimeOffset.UtcNow);
            Console.WriteLine($"[save] boot save {folder}: {check.Detail}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[save] boot save {folder}: CHECK FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static readonly TimeSpan CheckpointReadyWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckpointRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>A SAVE_GAME refusal that only means "not now": not in-world yet, saving not allowed yet, a save
    /// running, not in live mode yet (the mod's words, ServerHostMain.SavePreconditionFailure).</summary>
    internal static bool IsNotReadyToSave(string? reason) =>
        reason != null
        && (reason.Contains("requires an in-world session", StringComparison.Ordinal)
            || reason.Contains("CanSaveGame=false", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SaveGameInProgress=True", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SAVE_GAME requires live mode", StringComparison.Ordinal));

    private static List<string> SaveRoots()
    {
        List<string> roots = new();
        string? configured = Environment.GetEnvironmentVariable("SIMSONLINE_SERVER_SAVES_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            roots.Add(configured);
        }

        string sims3 = Path.Combine("Electronic Arts", "The Sims 3", "Saves");
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", sims3));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), sims3));
        return roots;
    }

    private static string EnsureSims3Extension(string saveName) =>
        saveName.EndsWith(".sims3", StringComparison.OrdinalIgnoreCase) ? saveName : saveName + ".sims3";

    private static string? FindNewestSaveFolder(string saveName) => FindNewestSaveFolder(saveName, SaveRoots());

    internal static string? FindNewestSaveFolder(string saveName, IEnumerable<string> roots)
    {
        string suffixed = EnsureSims3Extension(saveName);
        return roots
            .SelectMany(root => new[] { Path.Combine(root, suffixed), Path.Combine(root, suffixed[..^6]) })
            .Where(folder => Directory.Exists(folder) && Directory.GetFiles(folder, "*.nhd").Length > 0)
            .OrderByDescending(folder => Directory.GetFiles(folder, "*.nhd").Max(File.GetLastWriteTimeUtc))
            .FirstOrDefault();
    }

    /// <summary> step 5: PROBE_SIM_DETAILS in batches of SimDetailsWire.MaxSimsPerProbe.</summary>
    public async Task<IReadOnlyList<SimDetailsSample>> ProbeSimDetailsAsync(IReadOnlyList<ulong> simObjectIds, CancellationToken cancellationToken)
    {
        EnsureStarted();
        List<SimDetailsSample> samples = new();
        int batch = SimsOnlineServerHost.SimDetailsWire.MaxSimsPerProbe;
        for (int i = 0; i < simObjectIds.Count; i += batch)
        {
            List<ulong> ids = simObjectIds.Skip(i).Take(batch).ToList();
            BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimDetails(Guid.NewGuid(), ids), cancellationToken);
            if (!response.IsAccepted)
            {
                throw new InvalidOperationException($"PROBE_SIM_DETAILS rejected: {response.RejectionReason}");
            }

            samples.AddRange(SimDetailsSample.ParseProbeReport(response.ProbeReport));
        }

        return samples;
    }

    /// <summary> step 5: APPLY_SIM_DETAILS (client role).</summary>
    public async Task<string?> ApplySimDetailsAsync(SimDetailsSample details, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplySimDetails(Guid.NewGuid(), details), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_SIM_DETAILS rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>EXPORT_SIM_DESCRIPTION (server role), read back from the host BULK slot.</summary>
    public async Task<SimDescriptionPayload?> ExportSimDescriptionAsync(ulong simDescriptionId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        Guid correlationId = Guid.NewGuid();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ExportSimDescription(correlationId, simDescriptionId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"EXPORT_SIM_DESCRIPTION rejected: {response.RejectionReason}");
        }

        string report = response.ProbeReport ?? string.Empty;
        if (!report.Contains("result=exported", StringComparison.Ordinal))
        {
            return null;
        }

        int ReportInt(string key) => int.Parse(System.Text.RegularExpressions.Regex.Match(report, key + @"=(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        uint crc = uint.Parse(System.Text.RegularExpressions.Regex.Match(report, @"crc=([0-9A-Fa-f]+)").Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        HostBulkReadResult bulk = _bridge.ReadHostBulkPayload(correlationId, ReportInt("slot"), ReportInt("bytes"), crc);
        if (!bulk.IsSuccess)
        {
            throw new InvalidOperationException($"EXPORT_SIM_DESCRIPTION BULK read failed: {bulk.Error} {bulk.Detail}");
        }

        return SimDescriptionPayload.FromCombined(simDescriptionId, bulk.Payload!, ReportInt("props"), ReportInt("keys"), ReportInt("objects"))
            ?? throw new InvalidOperationException($"EXPORT_SIM_DESCRIPTION segment sizes don't match: {report}");
    }

    /// <summary>upload the description's bytes (BULK_REF), then IMPORT_SIM_DESCRIPTION (client role).</summary>
    public async Task<string?> ImportSimDescriptionAsync(SimDescriptionPayload payload, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse upload = await _bridge.UploadInboundBlobAsync($"desc-{payload.SimDescriptionId}", payload.Combined(), cancellationToken);
        if (!upload.IsAccepted)
        {
            throw new InvalidOperationException($"IMPORT_SIM_DESCRIPTION BULK upload rejected: {upload.RejectionReason}");
        }

        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ImportSimDescription(Guid.NewGuid(), payload), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"IMPORT_SIM_DESCRIPTION rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    /// <summary>W2 Sim identity: send SET_SIM_ALIASES (see IMirrorHost.SetSimAliasesAsync).</summary>
    public async Task<string?> SetSimAliasesAsync(IReadOnlyList<SimAlias> aliases, bool reset, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SetSimAliases(Guid.NewGuid(), aliases, reset), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_SIM_ALIASES rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no report)";
    }

    public async Task ApplySimStateAsync(SimStateSample state, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplySimState(Guid.NewGuid(), state), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_SIM_STATE rejected: {response.RejectionReason}");
        }
    }

    /// <summary>P2 autonomy probe: ask the script DLL to disable autonomy on the given Sim. Returns the strategy report.</summary>
    public async Task<string> ProbeDisableAutonomyAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.DisableAutonomy(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "DisableAutonomy probe rejected by Sims 3 host.");
        }
        return response.ProbeReport ?? "(no probe report)";
    }

    public async Task<string> ProbeEnableAutonomyAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.EnableAutonomy(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "EnableAutonomy probe rejected by Sims 3 host.");
        }
        return response.ProbeReport ?? "(no probe report)";
    }

    public async Task<string> ProbeSimLifecycleAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimLifecycle(Guid.NewGuid()), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "Sim lifecycle probe rejected by Sims 3 host.");
        }

        return response.ProbeReport ?? "(no probe report)";
    }

    public async Task<string> ProbeActorLookupParityAsync(CancellationToken cancellationToken)
    {
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeActorLookup(Guid.NewGuid()), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_ACTOR_LOOKUP rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? string.Empty;
    }

        public async Task SetRoleAsync(HostRole role, CancellationToken cancellationToken)
    {
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SetRole(Guid.NewGuid(), role), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_ROLE rejected: {response.RejectionReason}");
        }
    }

    public async Task ApplyMotiveAsync(ulong simObjectId, string motiveName, float value, CancellationToken cancellationToken)
    {
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplyMotive(Guid.NewGuid(), simObjectId, motiveName, value), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_MOTIVE rejected: {response.RejectionReason}");
        }
    }

    public async Task<float> ProbeSimMotiveAsync(ulong simObjectId, string motiveName, CancellationToken cancellationToken)
    {
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimMotive(Guid.NewGuid(), simObjectId, motiveName), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_SIM_MOTIVE rejected: {response.RejectionReason}");
        }
        // Report format: "motive=<name>;value=<float>"
        string report = response.ProbeReport ?? throw new InvalidOperationException("PROBE_SIM_MOTIVE returned no report");
        foreach (string field in report.Split(';'))
        {
            int eq = field.IndexOf('=');
            if (eq < 0) continue;
            string key = field.Substring(0, eq);
            string val = field.Substring(eq + 1);
            if (key == "value" && float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            {
                return parsed;
            }
        }
        throw new FormatException($"PROBE_SIM_MOTIVE report did not contain a parseable value: {report}");
    }

        public async Task ApplyPositionAsync(ulong simObjectId, Vector3Dto targetPosition, CancellationToken cancellationToken)
    {
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplyPosition(Guid.NewGuid(), simObjectId, targetPosition), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_POSITION rejected: {response.RejectionReason}");
        }
    }

    /// <summary>
    /// batched client-role position write. The translator pre-chunks each snapshot to the
    /// single-frame budget, so a typical call is already one frame; this method defensively re-chunks
    /// (no-op when the list already fits) so an over-budget caller still produces valid frames rather
    /// than a too-large frame the bridge would reject. Each chunk is one APPLY_POSITIONS round-trip.
    /// </summary>
    public async Task ApplyPositionsAsync(IReadOnlyList<(ulong SimObjectId, Vector3Dto Position)> positions, CancellationToken cancellationToken)
    {
        if (positions == null || positions.Count == 0)
        {
            return;
        }

        foreach (BridgeCommand.ApplyPositions chunk in BridgeLineCodec.ChunkApplyPositions(positions))
        {
            BridgeResponse response = await SendBridgeCommandAsync(chunk, cancellationToken);
            if (!response.IsAccepted)
            {
                throw new InvalidOperationException($"APPLY_POSITIONS rejected: {response.RejectionReason}");
            }
        }
    }

    /// <summary>M4 P4.1.1.2: client-role state write for Household.FamilyFunds.</summary>
    public async Task ApplyCashAsync(ulong householdId, int funds, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplyCash(Guid.NewGuid(), householdId, funds), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_CASH rejected: {response.RejectionReason}");
        }
    }

    /// <summary>M4 P4.1.1.2: read a household's current FamilyFunds value.</summary>
    public async Task<HouseholdCashProbeReport> ProbeHouseholdCashAsync(ulong householdId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeHouseholdCash(Guid.NewGuid(), householdId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_HOUSEHOLD_CASH rejected: {response.RejectionReason}");
        }

        string raw = response.ProbeReport ?? throw new InvalidOperationException("PROBE_HOUSEHOLD_CASH returned no report");
        return HouseholdCashProbeReport.Parse(raw);
    }

    /// <summary>M4 P4.1.1.3: client-role state write for Sim skill level.</summary>
    public async Task ApplySkillAsync(ulong simObjectId, string skillName, int level, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplySkill(Guid.NewGuid(), simObjectId, skillName, level), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_SKILL rejected: {response.RejectionReason}");
        }
    }

    /// <summary>M4 P4.1.1.3: read a Sim's current skill level + points.</summary>
    public async Task<SimSkillProbeReport> ProbeSimSkillAsync(ulong simObjectId, string skillName, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimSkill(Guid.NewGuid(), simObjectId, skillName), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_SIM_SKILL rejected: {response.RejectionReason}");
        }
        string raw = response.ProbeReport ?? throw new InvalidOperationException("PROBE_SIM_SKILL returned no report");
        return SimSkillProbeReport.Parse(raw);
    }

    // IAuthoritativeSimulationHost probe wrappers (nullable, best-effort).
    async Task<float?> IAuthoritativeSimulationHost.ProbeSimMotiveAsync(ulong simObjectId, string motiveName, CancellationToken cancellationToken)
    {
        try { return await ProbeSimMotiveAsync(simObjectId, motiveName, cancellationToken); }
        catch { return null; }
    }

    async Task<int?> IAuthoritativeSimulationHost.ProbeHouseholdFundsAsync(ulong householdId, CancellationToken cancellationToken)
    {
        try { return (await ProbeHouseholdCashAsync(householdId, cancellationToken)).Funds; }
        catch { return null; }
    }

    async Task<int?> IAuthoritativeSimulationHost.ProbeSimSkillAsync(ulong simObjectId, string skillName, CancellationToken cancellationToken)
    {
        try { return (await ProbeSimSkillAsync(simObjectId, skillName, cancellationToken)).Level; }
        catch { return null; }
    }
    /// <summary>M4 P4.3: client-role state write for Sim autonomy (idempotent absolute-set semantics).</summary>
    public async Task ApplyAutonomyAsync(ulong simObjectId, bool disabled, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplyAutonomy(Guid.NewGuid(), simObjectId, disabled), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_AUTONOMY rejected: {response.RejectionReason}");
        }
    }

    /// <summary>P5.0: time/speed authority — typed IMirrorHost surface for the existing SET_GAME_SPEED bridge command. Used by the client-role mirror loop to sync the local TS3 to the server-decided speed.</summary>
    public async Task SetGameSpeedAsync(GameSpeed speed, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SetGameSpeed(Guid.NewGuid(), speed), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_GAME_SPEED rejected: {response.RejectionReason}");
        }
    }

    /// <summary>absolute clock authority — apply server world time to a client-role mirror TS3.</summary>
    public async Task SetWorldTimeAsync(WorldTime time, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SetWorldTime(Guid.NewGuid(), time), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_WORLD_TIME rejected: {response.RejectionReason}");
        }
    }

    /// <summary>Dev tooling: show an in-game StyledNotification toast (best-effort UI sugar; script DLL swallows toast failures).</summary>
    public async Task ShowNotificationAsync(string text, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ShowNotification(Guid.NewGuid(), text), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"NOTIFY rejected: {response.RejectionReason}");
        }
    }

    /// <summary>SHOW_DIALOG (see IMirrorHost.ShowDialogAsync).</summary>
    public async Task ShowDialogAsync(int requestId, ulong simObjectId, string kind, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ShowDialog(Guid.NewGuid(), requestId, simObjectId, kind, args), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SHOW_DIALOG rejected: {response.RejectionReason}");
        }
    }

    /// <summary>SET_ACTIVE_SIM (see IMirrorHost.SetActiveSimAsync).</summary>
    public async Task SetActiveSimAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.SetActiveSim(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"SET_ACTIVE_SIM rejected: {response.RejectionReason}");
        }
    }

    public async Task<MirroredInteractionState?> QueryInteractionStateAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(
            new BridgeCommand.QueryInteractionState(Guid.NewGuid(), simObjectId),
            cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"DIAG_INTERACTION_STATE rejected: {response.RejectionReason}");
        }

        string raw = response.ProbeReport ?? throw new InvalidOperationException("DIAG_INTERACTION_STATE returned no report");
        return MirroredInteractionState.Parse(raw);
    }

    public async Task InjectReadIntentAsync(
        ulong simObjectId,
        ulong bookObjectId,
        string mode,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(
            new BridgeCommand.InjectReadIntent(Guid.NewGuid(), simObjectId, bookObjectId, mode, parameters),
            cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"INJECT_READ_INTENT rejected: {response.RejectionReason}");
        }
    }

    /// <summary>M4 P4.3: read a Sim's current autonomy state.</summary>
    public async Task<SimAutonomyProbeReport> ProbeSimAutonomyAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimAutonomy(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_SIM_AUTONOMY rejected: {response.RejectionReason}");
        }
        string raw = response.ProbeReport ?? throw new InvalidOperationException("PROBE_SIM_AUTONOMY returned no report");
        return SimAutonomyProbeReport.Parse(raw);
    }

        public async Task<string> ProbeStateInjectionAsync(ulong simObjectId, Vector3Dto targetPosition, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeStateInjection(Guid.NewGuid(), simObjectId, targetPosition), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException(response.RejectionReason ?? "State injection probe rejected by Sims 3 host.");
        }

        return response.ProbeReport ?? "(no probe report)";
    }

    /// <summary>
    /// M4 P4.1.1.1: client-role state write. Queues an interaction onto a Sim using the
    /// same payload shape as the existing public-protocol QueueInteraction, but routes
    /// through the role-gated APPLY_INTERACTION_QUEUE wire command (script DLL enforces
    /// sCurrentRole=="client").
    /// </summary>
    public Task ApplyInteractionQueueAsync(ulong actorObjectId, string interactionKey, ulong? targetObjectId, Vector3Dto? targetPosition, CancellationToken cancellationToken) =>
        ApplyInteractionQueueAsync(
            new InteractionRequest(actorObjectId, interactionKey, targetObjectId, targetPosition, new Dictionary<string, string>()),
            cancellationToken);

    public async Task ApplyInteractionQueueAsync(InteractionRequest interaction, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplyInteractionQueue(Guid.NewGuid(), interaction), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_INTERACTION_QUEUE rejected: {response.RejectionReason}");
        }
    }

    /// <summary>
    /// batched client-role interaction replay. One or more APPLY_INTERACTIONS frames
    /// when the payload exceeds the 4080-byte budget (mirrors ApplyPositionsAsync chunking).
    /// </summary>
    public async Task ApplyInteractionsAsync(IReadOnlyList<InteractionRequest> interactions, CancellationToken cancellationToken)
    {
        if (interactions == null || interactions.Count == 0)
        {
            return;
        }

        foreach (BridgeCommand.ApplyInteractions chunk in BridgeLineCodec.ChunkApplyInteractions(interactions))
        {
            BridgeResponse response = await SendBridgeCommandAsync(chunk, cancellationToken);
            if (!response.IsAccepted)
            {
                throw new InvalidOperationException($"APPLY_INTERACTIONS rejected: {response.RejectionReason}");
            }
        }
    }

    /// <summary>
    /// client-role posture/container alignment from an authoritative replay bundle.
    /// Routes through APPLY_FORCE_ALIGN (Sit transition on postureContainerObjectId first).
    /// </summary>
    public async Task ApplyForceAlignAsync(InteractionReplayBundle bundle, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ApplyForceAlign(Guid.NewGuid(), bundle), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"APPLY_FORCE_ALIGN rejected: {response.RejectionReason}");
        }
    }

    /// <summary>
    /// M4 P4.1.1.1: read a Sim's current interaction queue state. Returns the parsed
    /// report (sim id, queueCount, current interaction type name). Used by
    /// --probe-interaction-roundtrip to verify APPLY_INTERACTION_QUEUE landed.
    /// </summary>
    public async Task<InteractionQueueProbeReport> ProbeSimInteractionQueueAsync(ulong simObjectId, CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.ProbeSimInteractionQueue(Guid.NewGuid(), simObjectId), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"PROBE_SIM_INTERACTION_QUEUE rejected: {response.RejectionReason}");
        }

        string raw = response.ProbeReport ?? throw new InvalidOperationException("PROBE_SIM_INTERACTION_QUEUE returned no report");
        return InteractionQueueProbeReport.Parse(raw);
    }

    /// <summary>
    /// read-only mod-state report for re-attach (role, in-world, tick, autonomy-watched
    /// sims, pending outbound count). A re-attached controller starts with empty claim/role maps;
    /// this is how it sees what the previous session left behind.
    /// </summary>
    public async Task<string> DiagStateAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        BridgeResponse response = await SendBridgeCommandAsync(new BridgeCommand.DiagState(Guid.NewGuid()), cancellationToken);
        if (!response.IsAccepted)
        {
            throw new InvalidOperationException($"DIAG_STATE rejected: {response.RejectionReason}");
        }

        return response.ProbeReport ?? "(no diag report)";
    }

    /// <summary>
    /// heavy commands (load/save, chunked snapshots, long-running probes) keep the
    /// long host-bridge timeout; everything else is player-paced and must fail in seconds —
    /// a stuck command holds the serialized bridge write-lock for its whole timeout, so a
    /// 600s player command wedged every queued command behind it.
    /// </summary>
    public static TimeSpan CommandTimeoutFor(BridgeCommand command, TimeSpan hostBridgeTimeout, TimeSpan playerCommandTimeout)
    {
        return command switch
        {
            BridgeCommand.LoadSave
                or BridgeCommand.LoadSaveViaMenu
                or BridgeCommand.SaveGame
                or BridgeCommand.RequestSnapshot
                or BridgeCommand.ProbeSimLifecycle
                or BridgeCommand.ProbeActorLookup
                or BridgeCommand.ProbeStateInjection => hostBridgeTimeout,
            BridgeCommand.ApplyForceAlign => SessionConfig.DefaultForceAlignCommandTimeout,
            _ => playerCommandTimeout,
        };
    }

    private async Task<BridgeResponse> SendBridgeCommandAsync(BridgeCommand command, CancellationToken cancellationToken)
    {
        TimeSpan commandTimeout = CommandTimeoutFor(command, _config.HostBridgeTimeout, _config.EffectivePlayerCommandTimeout);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(commandTimeout);

        try
        {
            return await _bridge.SendAsync(command, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only this command's own timeout is a TimeoutException; a send the bridge cancelled because it is
            // shutting down stays a cancellation. when a chunked response times out, the reassembly
            // state distinguishes tick starvation (received well short of total) from chunk loss (nearly
            // complete with specific missing indices).
            string? chunkState = _bridge.DescribeChunkState(command.CorrelationId);
            string suffix = chunkState != null ? $" Reassembly state: {chunkState}." : string.Empty;
            throw new TimeoutException($"Timed out waiting {commandTimeout.TotalSeconds:N0} seconds for the Sims 3 host response to {command.GetType().Name}.{suffix}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;

        // Graceful disconnect BEFORE any process teardown: in attach mode the game outlives
        // this controller, and the mod must learn the session ended (resets its inbound
        // dedupe cursor + re-arms the discovery beacon immediately). Harmless in launch mode.
        _bridge.SendDisconnectBestEffort();

        try
        {
            await StopOwnedHostProcessAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopOwnedHostProcessAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        if (!_ownsHostProcess || _hostProcess == null)
        {
            return;
        }

        bool hasExited;
        try
        {
            hasExited = _hostProcess.HasExited;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (hasExited)
        {
            return;
        }

        bool closeRequested;
        try
        {
            closeRequested = _hostProcess.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!closeRequested && !HasExitedSafely(_hostProcess))
        {
            _hostProcess.Kill(entireProcessTree: true);
            await _hostProcess.WaitForExitAsync(cancellationToken);
            return;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(gracefulTimeout);

        try
        {
            await _hostProcess.WaitForExitAsync(timeout.Token);
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
            if (!HasExitedSafely(_hostProcess))
            {
                try
                {
                    _hostProcess.Kill(entireProcessTree: true);
                    await _hostProcess.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }

    private static bool HasExitedSafely(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new InvalidOperationException("The Sims 3 host has not been started.");
        }
    }

    private Process AttachToExistingHost()
    {
        // with the two-TS3 dev stack (server + Sandboxie mirror client) attaching to
        // the first TS3W by name can pick the WRONG instance — pin by PID when provided
        // (dev-session tracks the server TS3 PID in .omo/run/server-ts3.pid).
        if (_config.AttachPid is int pid)
        {
            Process pinned;
            try
            {
                pinned = Process.GetProcessById(pid);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"--attach-pid {pid} does not name a running process.", ex);
            }

            // Wine keeps the PE file name as the Linux process name, so "TS3W.exe" is the same host.
            if (!string.Equals(pinned.ProcessName, "TS3W", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(pinned.ProcessName, "TS3W.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"--attach-pid {pid} is process '{pinned.ProcessName}', expected TS3W.");
            }

            _ownsHostProcess = false;
            return pinned;
        }

        Process? process = NativeProcessMemory.GetProcessesByWindowsName("TS3W").FirstOrDefault();
        if (process == null)
        {
            throw new InvalidOperationException("No running TS3W.exe process was found to attach as the dedicated host.");
        }

        _ownsHostProcess = false;
        return process;
    }

    private Process LaunchHostProcess(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_config.Ts3SandboxieBoxName))
        {
            return LaunchSandboxedHostProcess(cancellationToken);
        }

        if (!string.IsNullOrEmpty(_config.Ts3SandboxieStartPath))
        {
            throw new InvalidOperationException("--ts3-sandboxie-start requires --ts3-sandboxie-box <name>.");
        }

        ProcessStartInfo startInfo = BuildHostStartInfo(_config.Ts3ExecutablePath);

        // On Windows Process.Start returns the game process directly. Under Wine the launched
        // `wine <TS3W.exe>` is only a loader stub — the real TS3W.exe double-forks and reparents
        // (to systemd), so its PID differs from what Process.Start hands back. Discover the actual
        // game by watching for a new TS3W.exe (matching the Sandboxie discovery approach).
        if (!OperatingSystem.IsWindows())
        {
            return LaunchWineHostProcess(startInfo, cancellationToken);
        }

        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("TS3W.exe did not start.");
        _ownsHostProcess = true;
        return process;
    }

    private Process LaunchWineHostProcess(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        // Must happen BEFORE the launch: the game reparents when Wine's loader exits, and only
        // an already-armed subreaper catches it (keeps ptrace legal under default yama scope=1).
        HostBridge.NativeProcessMemory.TryEnableChildSubreaper();

        const string processName = "TS3W";
        HashSet<int> existingProcessIds = SnapshotProcessIds(processName);

        Process launcher = Process.Start(startInfo) ?? throw new InvalidOperationException("wine TS3W.exe did not start.");

        Process? process = WaitForNewHostProcess(processName, _config.Ts3ExecutablePath, existingProcessIds, WineProcessDiscoveryTimeout, cancellationToken);
        if (process == null)
        {
            try
            {
                if (!HasExitedSafely(launcher))
                {
                    launcher.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                launcher.Dispose();
            }

            throw new TimeoutException(
                $"wine launch did not expose a new TS3W.exe process within {WineProcessDiscoveryTimeout.TotalSeconds:N0} seconds. " +
                $"WINEPREFIX='{Environment.GetEnvironmentVariable("WINEPREFIX")}', TS3='{_config.Ts3ExecutablePath}'.");
        }

        // The wine loader stub exits once the game is up; we own the discovered game process.
        launcher.Dispose();
        _ownsHostProcess = true;
        return process;
    }

    /// <summary>
    /// On Windows TS3W.exe starts directly. On Linux the game lives inside a Wine prefix, so the
    /// launch is `wine <TS3W.exe>` with WINEPREFIX inherited from the environment (matching how
    /// the operator launches the game by hand). The child-process relationship this creates is
    /// also what lets the process-memory bridge read the TS3 heap under yama ptrace_scope=1
    /// without extra capabilities.
    /// 2026-07-03 two-client Linux: when <see cref="SessionConfig.ClientMirrorWineprefixPath"/>
    /// is set (the client controller launching its own isolated TS3), override WINEPREFIX on the
    /// launched wine process so the second Sims instance runs in the client prefix, isolated from
    /// the server's prefix. Falls through to the inherited env when null (the server path).
    /// </summary>
    private ProcessStartInfo BuildHostStartInfo(string ts3ExecutablePath)
    {
        string resolvedPath = ResolveHostExecutablePath(ts3ExecutablePath, _config.ClientMirrorWineprefixPath);
        string? workingDirectory = Path.GetDirectoryName(resolvedPath);
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = resolvedPath,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false
            };
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "wine",
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(resolvedPath);
        // The two-client stack isolates the second TS3 by launching wine with a different
        // WINEPREFIX (Sandboxie's role on Windows). Without this, the second TS3W.exe launch
        // in the same prefix gets single-instance-redirected or hits a Documents conflict.
        if (!string.IsNullOrEmpty(_config.ClientMirrorWineprefixPath))
        {
            startInfo.Environment["WINEPREFIX"] = _config.ClientMirrorWineprefixPath!;
        }
        return startInfo;
    }

    /// <summary>
    /// Maps a configured Windows-style TS3 path (the scripts' default
    /// C:\Program Files (x86)\The Sims 3\Game\Bin\TS3W.exe) onto the Wine prefix's drive_c when
    /// running on Linux. Native paths pass through untouched, so --ts3 can also point directly at
    /// the prefix file.
    /// The prefix must be the SAME one the wine child will run with: when
    /// <paramref name="wineprefixOverride"/> is set (the client controller's isolated prefix,
    /// see <see cref="SessionConfig.ClientMirrorWineprefixPath"/>), resolve against it instead of
    /// the controller's inherited WINEPREFIX — otherwise the client launches the SERVER prefix's
    /// TS3W.exe binary with client-prefix registry/Documents, breaking the install-dir isolation
    /// (scriptCache under Program Files) that bootstrap-client-prefix.ps1 sets up.
    /// </summary>
    private static string ResolveHostExecutablePath(string ts3ExecutablePath, string? wineprefixOverride)
    {
        if (OperatingSystem.IsWindows() || File.Exists(ts3ExecutablePath))
        {
            return ts3ExecutablePath;
        }

        if (ts3ExecutablePath.Length >= 3 && ts3ExecutablePath[1] == ':' && (ts3ExecutablePath[2] == '\\' || ts3ExecutablePath[2] == '/'))
        {
            string prefix = !string.IsNullOrEmpty(wineprefixOverride)
                ? wineprefixOverride!
                : Environment.GetEnvironmentVariable("WINEPREFIX")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
            string relative = ts3ExecutablePath[3..].Replace('\\', '/');
            string candidate = Path.Combine(prefix, "drive_c", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return ts3ExecutablePath;
    }

    private Process LaunchSandboxedHostProcess(CancellationToken cancellationToken)
    {
        string boxName = _config.Ts3SandboxieBoxName ?? throw new InvalidOperationException("--ts3-sandboxie-box must be non-empty.");
        string sandboxieStartPath = string.IsNullOrEmpty(_config.Ts3SandboxieStartPath)
            ? DefaultSandboxieStartPath
            : _config.Ts3SandboxieStartPath!;
        if (!File.Exists(sandboxieStartPath))
        {
            throw new FileNotFoundException("Could not find Sandboxie Start.exe for the Sims 3 host launch.", sandboxieStartPath);
        }

        string processName = Path.GetFileNameWithoutExtension(_config.Ts3ExecutablePath);
        HashSet<int> existingProcessIds = SnapshotProcessIds(processName);
        ProcessStartInfo startInfo = BuildSandboxieStartInfo(sandboxieStartPath, boxName, _config.Ts3ExecutablePath);

        using Process launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Sandboxie Start.exe did not start.");

        Process? process = WaitForNewHostProcess(processName, _config.Ts3ExecutablePath, existingProcessIds, SandboxieProcessDiscoveryTimeout, cancellationToken);
        if (process == null)
        {
            try
            {
                if (!HasExitedSafely(launcher))
                {
                    launcher.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            throw new TimeoutException(
                $"Sandboxie launch did not expose a new {processName}.exe process within {SandboxieProcessDiscoveryTimeout.TotalSeconds:N0} seconds. " +
                $"Box='{boxName}', Start='{sandboxieStartPath}', TS3='{_config.Ts3ExecutablePath}'.");
        }

        _ownsHostProcess = true;
        return process;
    }

    private static ProcessStartInfo BuildSandboxieStartInfo(string sandboxieStartPath, string boxName, string ts3ExecutablePath)
    {
        string? workingDirectory = Path.GetDirectoryName(ts3ExecutablePath);
        ProcessStartInfo startInfo = new()
        {
            FileName = sandboxieStartPath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add($"/box:{boxName}");
        startInfo.ArgumentList.Add(ts3ExecutablePath);
        return startInfo;
    }

    private static HashSet<int> SnapshotProcessIds(string processName)
    {
        HashSet<int> processIds = new();
        foreach (Process process in HostBridge.NativeProcessMemory.GetProcessesByWindowsName(processName))
        {
            using (process)
            {
                processIds.Add(process.Id);
            }
        }
        return processIds;
    }

    private static Process? WaitForNewHostProcess(
        string processName,
        string expectedExecutablePath,
        HashSet<int> existingProcessIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process? process = FindNewHostProcess(processName, expectedExecutablePath, existingProcessIds);
            if (process != null)
            {
                return process;
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return null;
    }

    private static Process? FindNewHostProcess(string processName, string expectedExecutablePath, HashSet<int> existingProcessIds)
    {
        List<Process> exactMatches = new();
        foreach (Process process in HostBridge.NativeProcessMemory.GetProcessesByWindowsName(processName))
        {
            if (existingProcessIds.Contains(process.Id) || HasExitedSafely(process))
            {
                process.Dispose();
                continue;
            }

            if (HasExecutablePath(process, expectedExecutablePath))
            {
                exactMatches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        if (exactMatches.Count == 0)
        {
            return null;
        }

        if (exactMatches.Count == 1)
        {
            return exactMatches[0];
        }

        string pids = string.Join(", ", exactMatches.Select(process => process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (Process process in exactMatches)
        {
            process.Dispose();
        }
        throw new InvalidOperationException(
            $"Sandboxie launch found multiple new {processName}.exe processes matching '{expectedExecutablePath}' (PIDs: {pids}). Refusing to choose one to avoid attaching to or killing the wrong Sims instance.");
    }

    private static bool HasExecutablePath(Process process, string expectedExecutablePath)
    {
        // On Linux a Wine process's /proc/pid/exe (MainModule) points at the Wine loader, not
        // TS3W.exe, so a WINDOWS-STYLE expected path (drive letter) cannot be verified that way.
        // The process COMMAND LINE still carries the PE path (the reparented game shows
        // "C:\...\TS3W.exe"), so verify against that instead of accepting blindly — an unrelated
        // same-named process without the expected exe on its command line stays rejected.
        if (!OperatingSystem.IsWindows()
            && expectedExecutablePath.Length >= 2 && expectedExecutablePath[1] == ':')
        {
            return WineCommandLineMentionsExecutable(process.Id, expectedExecutablePath);
        }

        try
        {
            string? actualPath = process.MainModule?.FileName;
            return actualPath != null
                && string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool WineCommandLineMentionsExecutable(int processId, string expectedExecutablePath)
    {
        string expectedName = FileNameFromAnyPath(expectedExecutablePath);
        try
        {
            string cmdlinePath = $"/proc/{processId}/cmdline";
            if (!File.Exists(cmdlinePath))
            {
                return false;
            }

            string[] args = File.ReadAllText(cmdlinePath).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            // The exe may appear as arg0 ("C:\...\TS3W.exe" after reparent) or as the wine
            // loader's argument ("wine TS3W.exe" stub) — match the basename anywhere.
            return args.Any(arg => string.Equals(FileNameFromAnyPath(arg), expectedName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string FileNameFromAnyPath(string path)
    {
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? path[(cut + 1)..] : path;
    }
}
