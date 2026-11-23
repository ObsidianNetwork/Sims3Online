using System.Globalization;
using System.Text;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.HostBridge;

public static class BridgeLineCodec
{
    // Bridge-only planning traffic: HostBridgeServer consumes this before public EVENT decoding.
    public static bool IsRoadPlaceRequest(string line) => line.TrimStart('\uFEFF').StartsWith("EVENT|RoadPlaceRequest|", StringComparison.Ordinal);
    public static bool TryParseRoadPlaceRequest(string line, out RoadPlaceRequest request) => RoadPlaceRequest.TryParse(line, out request);

    // 10: consumed-marker handshake (controller stamps consumed sender-id; mod gates
    //     outbound writes on it). Older mods ignore the stamp (legacy lossy behavior); newer
    //     mods degrade to legacy after 500ms against older controllers.
    // 11: batched APPLY_POSITIONS — one framed command carries N Sim positions, applied
    //     in a single simulator tick, replacing N serial APPLY_POSITION round-trips per snapshot.
    // 12: batched APPLY_INTERACTIONS — N interaction replays in one frame / one tick.
    // 13: APPLY_FORCE_ALIGN - posture/container correction for Sit transitions.
    // 14: exact-seat ForceAlign parameters on APPLY_FORCE_ALIGN.
    // 15: optional replay parameters on APPLY_INTERACTION_QUEUE / APPLY_INTERACTIONS.
    // 16: DIAG_INTERACTION_STATE posture/progress watchdog report.
    // 17: SET_WORLD_TIME absolute clock authority (server + mirror).
    // 18: snapshot sim items add interactionQueueCount (11th colon field);
    //     v11 multi-slot control ring + versioned CONNECT / HELLO|6 caps negotiation.
    // 19: INJECT_READ_INTENT carries optional replay parameters for host autonomy.
    // 20: slice 6 MERGE_LOT_STATE — server-role lot-boundary reconcile write applying a
    //     client-native owned-lot class A bundle into the server TS3 before SAVE_GAME.
    // 21: deep household IMPORT/EXPORT metadata over control frames with payloads in
    //     the existing 512 KiB BULK slot.
    // 22: adds MERGE_TRAVELLING_HOUSEHOLD (reload-safe replacement for IMPORT_HOUSEHOLD_CONTENT).
    // 23: W2 Sim identity: snapshot Sim items end with simDescriptionId (12th colon field; SNAPSHOT2
    //     version 2) and SET_SIM_ALIASES maps server ObjectIds to descriptions on a client.
    public const int ProtocolVersion = 23;

    // the bridge moves one ~4 KB frame at a time. ProcessMemoryBridge.WriteFrame caps the
    // encoded command at BufferSize(4096) - HeaderSize(16) = 4080 bytes. Batched commands are chunked
    // to stay within this; encoded commands are pure ASCII so char count == byte count.
    public const int ApplyPositionsFrameBudgetBytes = 4080;
    public const int ApplyInteractionsFrameBudgetBytes = 4080;
    private static readonly string[] ForceAlignParameterWireKeys =
    [
        "hitX",
        "hitY",
        "hitZ",
        "seatArea",
        "seatContainmentSlot",
        "seatIkSuffix",
        "seatRoutingSlot",
        "autonomous",
        "userDirected",
        "effectivelyAutonomous",
    ];
    private static readonly string[] InteractionParameterWireKeys =
    [
        "bookId",
        "progressNormalized",
        "autonomous",
        "userDirected",
        "effectivelyAutonomous",
        // seat pin for the book-chain INJECT_READ_INTENT. The client engine's
        // Sit.GetContainmentSlotClosestToHit falls back to the Sim's own position when the
        // hit point is zero, so without these it picks the empty slot nearest the *client*
        // Sim and diverges from the host's seat. Carrying the host Sit's seat object + slot
        // lets the mod aim a native Sit at the host slot's world position (decision-injection
        // preserved -- we only pin the target seat, never force-set posture).
        "seatObjectId",
        "seatArea",
        "seatContainmentSlot",
        "seatRoutingSlot",
        "seatIkSuffix",
    ];

    public static string EncodeCommand(BridgeCommand command)
    {
        return command switch
        {
            BridgeCommand.LoadSave loadSave =>
                Join("LOAD_SAVE", Id(loadSave.CorrelationId), Text(loadSave.SaveName)),

            BridgeCommand.LoadSaveViaMenu loadSaveViaMenu =>
                Join("LOAD_SAVE_MENU", Id(loadSaveViaMenu.CorrelationId), Text(loadSaveViaMenu.SaveName)),

            BridgeCommand.SaveGame saveGame =>
                Join("SAVE_GAME", Id(saveGame.CorrelationId), Text(saveGame.SaveName)),

            BridgeCommand.SetGameSpeed setGameSpeed =>
                Join("SET_GAME_SPEED", Id(setGameSpeed.CorrelationId), EncodeSpeed(setGameSpeed.Speed)),

            BridgeCommand.SetWorldTime setWorldTime =>
                Join(
                    "SET_WORLD_TIME",
                    Id(setWorldTime.CorrelationId),
                    setWorldTime.Time.Day.ToString(CultureInfo.InvariantCulture),
                    setWorldTime.Time.Hours.ToString(CultureInfo.InvariantCulture),
                    setWorldTime.Time.Minutes.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.RequestSnapshot requestSnapshot =>
                requestSnapshot.SinceTick.HasValue
                    ? Join("REQUEST_SNAPSHOT", Id(requestSnapshot.CorrelationId), requestSnapshot.SinceTick.Value.ToString(CultureInfo.InvariantCulture))
                    : Join("REQUEST_SNAPSHOT", Id(requestSnapshot.CorrelationId)),

            BridgeCommand.ProbeSimLifecycle probeLifecycle =>
                Join("PROBE_SIM_LIFECYCLE", Id(probeLifecycle.CorrelationId)),

            BridgeCommand.ProbeActorLookup probeActorLookup =>
                Join("PROBE_ACTOR_LOOKUP", Id(probeActorLookup.CorrelationId)),

            BridgeCommand.ProbeStateInjection stateInjection =>
                Join(
                    "PROBE_STATE_INJECTION",
                    Id(stateInjection.CorrelationId),
                    stateInjection.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    Float(stateInjection.TargetPosition.X),
                    Float(stateInjection.TargetPosition.Y),
                    Float(stateInjection.TargetPosition.Z)),

            BridgeCommand.QueueInteraction queueInteraction =>
                EncodeQueueInteraction(queueInteraction),

            BridgeCommand.SetActiveSim setActiveSim =>
                Join("SET_ACTIVE_SIM", Id(setActiveSim.CorrelationId), setActiveSim.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.AnswerDialog answerDialog =>
                Join("DIALOG_ANSWER", Id(answerDialog.CorrelationId), answerDialog.RequestId.ToString(CultureInfo.InvariantCulture),
                    answerDialog.Answer == null ? "-" : Text(answerDialog.Answer)),

            BridgeCommand.ShowDialog showDialog =>
                Join(new[]
                {
                    "SHOW_DIALOG", Id(showDialog.CorrelationId), showDialog.RequestId.ToString(CultureInfo.InvariantCulture),
                    showDialog.SimObjectId.ToString(CultureInfo.InvariantCulture), Text(showDialog.Kind),
                    showDialog.Args.Count.ToString(CultureInfo.InvariantCulture),
                }.Concat(showDialog.Args.Select(Text)).ToArray()),

            BridgeCommand.CancelInteraction cancelInteraction =>
                Join(
                    "CANCEL_INTERACTION",
                    Id(cancelInteraction.CorrelationId),
                    cancelInteraction.ActorObjectId.ToString(CultureInfo.InvariantCulture),
                    cancelInteraction.InteractionId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.LeaveConversation leaveConversation =>
                Join(
                    "LEAVE_CONVERSATION",
                    Id(leaveConversation.CorrelationId),
                    leaveConversation.ActorObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.DisableAutonomy disable =>
                Join("DISABLE_AUTONOMY", Id(disable.CorrelationId), disable.SimObjectId.ToString(CultureInfo.InvariantCulture), disable.CancelRunning ? "1" : "0"),

            BridgeCommand.EnableAutonomy enable =>
                enable.Reset
                    ? Join("ENABLE_AUTONOMY", Id(enable.CorrelationId), enable.SimObjectId.ToString(CultureInfo.InvariantCulture), "reset")
                    : Join("ENABLE_AUTONOMY", Id(enable.CorrelationId), enable.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            // command-lifecycle autonomy gate register/clear.
            BridgeCommand.GateAutonomy gate =>
                Join("GATE_AUTONOMY", Id(gate.CorrelationId), gate.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.UngateAutonomy ungate =>
                ungate.Reset
                    ? Join("UNGATE_AUTONOMY", Id(ungate.CorrelationId), ungate.SimObjectId.ToString(CultureInfo.InvariantCulture), "reset")
                    : Join("UNGATE_AUTONOMY", Id(ungate.CorrelationId), ungate.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            // W1: server simulation scope (player-occupied lots, player-owned Sims).
            BridgeCommand.SetSimScope scope =>
                Join("SET_SIM_SCOPE", Id(scope.CorrelationId),
                    SimsOnlineServerHost.SimScopeWire.Format(scope.LotIds.ToList()),
                    SimsOnlineServerHost.SimScopeWire.Format(scope.SimObjectIds.ToList())),

            // W2: client authority gate (mode + owned Sims).
            BridgeCommand.SetAuthority authority =>
                Join("SET_AUTHORITY", Id(authority.CorrelationId),
                    authority.Mode.ToString().ToLowerInvariant(),
                    SimsOnlineServerHost.SimScopeWire.Format(authority.OwnedSimObjectIds.ToList())),

            // W2 part B: visible-state mirror for Sims a client does not own.
            BridgeCommand.ProbeSimState probeState =>
                Join("PROBE_SIM_STATE", Id(probeState.CorrelationId),
                    SimsOnlineServerHost.SimScopeWire.Format(probeState.SimObjectIds.ToList())),

            BridgeCommand.ApplySimState applyState =>
                Join("APPLY_SIM_STATE", Id(applyState.CorrelationId), applyState.State.ToWireEntry()),

            // W2 Sim identity: server ObjectId -> SimDescriptionId on a client.
            // W2 Sim identity step 3: presence journal (server), spawn/despawn (client).
            BridgeCommand.ProbeSimPresence presence =>
                Join("PROBE_SIM_PRESENCE", Id(presence.CorrelationId), presence.SinceSeq.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.SpawnSim spawn =>
                Join("SPAWN_SIM", Id(spawn.CorrelationId),
                    spawn.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    spawn.SimDescriptionId.ToString(CultureInfo.InvariantCulture),
                    spawn.Position.X.ToString("R", CultureInfo.InvariantCulture),
                    spawn.Position.Y.ToString("R", CultureInfo.InvariantCulture),
                    spawn.Position.Z.ToString("R", CultureInfo.InvariantCulture)),

            BridgeCommand.DespawnSim despawn =>
                Join("DESPAWN_SIM", Id(despawn.CorrelationId),
                    despawn.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    despawn.SimDescriptionId.ToString(CultureInfo.InvariantCulture)),

            // one Sim description, server -> client, through the BULK slots.
            BridgeCommand.ProbeSimDetails probeDetails =>
                Join("PROBE_SIM_DETAILS", Id(probeDetails.CorrelationId),
                    SimsOnlineServerHost.SimScopeWire.Format(probeDetails.SimObjectIds.ToList())),

            BridgeCommand.ApplySimDetails applyDetails =>
                Join("APPLY_SIM_DETAILS", Id(applyDetails.CorrelationId), applyDetails.Details.ToWireEntry()),

            BridgeCommand.ExportSimDescription export =>
                Join("EXPORT_SIM_DESCRIPTION", Id(export.CorrelationId), export.SimDescriptionId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ImportSimDescription import =>
                Join("IMPORT_SIM_DESCRIPTION", Id(import.CorrelationId),
                    import.Payload.SimDescriptionId.ToString(CultureInfo.InvariantCulture),
                    (import.Payload.PropertyStream.Length + import.Payload.ResKeyTable.Length + import.Payload.ObjectIdTable.Length).ToString(CultureInfo.InvariantCulture),
                    import.Payload.PropertyStream.Length.ToString(CultureInfo.InvariantCulture),
                    import.Payload.ResKeyTable.Length.ToString(CultureInfo.InvariantCulture),
                    import.Payload.ObjectIdTable.Length.ToString(CultureInfo.InvariantCulture),
                    import.Payload.Crc32().ToString("X8", CultureInfo.InvariantCulture)),

            // presentation stream: recorder (server) and replay (client).
            BridgeCommand.SacsRecorder recorder =>
                Join(new[] { "SACS_RECORDER", Id(recorder.CorrelationId) }.Concat(recorder.Args).ToArray()),

            BridgeCommand.SacsReplay replay =>
                Join("SACS_REPLAY", Id(replay.CorrelationId), Mirroring.PresentationStream.EncodeReplay(replay.Lines)),

            BridgeCommand.SetSimAliases aliases => aliases.Reset
                ? Join("SET_SIM_ALIASES", Id(aliases.CorrelationId), FormatSimAliases(aliases.Aliases), "reset")
                : Join("SET_SIM_ALIASES", Id(aliases.CorrelationId), FormatSimAliases(aliases.Aliases)),

            BridgeCommand.DiagState diagState =>
                Join("DIAG_STATE", Id(diagState.CorrelationId)),

            BridgeCommand.DiagCaptureInteraction capture =>
                Join("DIAG_CAPTURE_INTERACTION", Id(capture.CorrelationId), capture.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.QueryInteractionState queryState =>
                queryState.WatchBookObjectId.HasValue
                    ? Join(
                        "DIAG_INTERACTION_STATE",
                        Id(queryState.CorrelationId),
                        queryState.SimObjectId.ToString(CultureInfo.InvariantCulture),
                        Text(queryState.WatchBookObjectId.Value.ToString(CultureInfo.InvariantCulture)))
                    : Join(
                        "DIAG_INTERACTION_STATE",
                        Id(queryState.CorrelationId),
                        queryState.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.InjectReadIntent injectRead =>
                EncodeInjectReadIntent(injectRead),

            BridgeCommand.Ping ping =>
                Join("PING", Id(ping.CorrelationId)),

            BridgeCommand.Disconnect disconnect =>
                Join("DISCONNECT", Id(disconnect.CorrelationId)),

            BridgeCommand.Raw raw =>
                EncodeRawCommand(raw),

            BridgeCommand.SetRole setRole =>
                Join("SET_ROLE", Id(setRole.CorrelationId), EncodeRole(setRole.Role)),

            BridgeCommand.ApplyPosition applyPos =>
                Join(
                    "APPLY_POSITION",
                    Id(applyPos.CorrelationId),
                    applyPos.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    Float(applyPos.TargetPosition.X),
                    Float(applyPos.TargetPosition.Y),
                    Float(applyPos.TargetPosition.Z)),

            // APPLY_POSITIONS|<corr>|<simId:x:y:z>;<simId:x:y:z>;... — a single frame for
            // N Sims. The caller is responsible for keeping each command within the frame budget
            // (use ChunkApplyPositions); EncodeCommand itself does not split.
            BridgeCommand.ApplyPositions applyPositions =>
                Join(
                    "APPLY_POSITIONS",
                    Id(applyPositions.CorrelationId),
                    EncodePositionPairs(applyPositions.Positions)),

            // slice 6: MERGE_LOT_STATE|<corr>|<lotId>|<simId:x:y:z>;... — server-role
            // lot-boundary reconcile write before SAVE_GAME. Same pair encoding as
            // APPLY_POSITIONS; the caller chunks oversized bundles via ChunkMergeLotState.
            BridgeCommand.MergeLotState mergeLotState =>
                Join(
                    "MERGE_LOT_STATE",
                    Id(mergeLotState.CorrelationId),
                    mergeLotState.Bundle.LotId.ToString(CultureInfo.InvariantCulture),
                    EncodeLotSimExports(mergeLotState.Bundle.Sims)),

            // IMPORT_HOUSEHOLD_CONTENT — control frame carrying only the
            // header metadata for the BULK-slot deep import. The mod fetches the uploaded blob
            // and runs Household.ImportContent + PostImport fixups. Wire:
            // IMPORT_HOUSEHOLD_CONTENT|<corr>|<householdId>|<totalBytes>|<propertyStreamBytes>|<resKeyTableBytes>|<objectIdTableBytes>|<crc32Hex>|<entryCount>|<resKeyCount>|<objectIdCount>
            BridgeCommand.ImportHouseholdContent importContent =>
                Join(
                    "IMPORT_HOUSEHOLD_CONTENT",
                    Id(importContent.CorrelationId),
                    importContent.HouseholdId.ToString(CultureInfo.InvariantCulture),
                    importContent.TotalBytes.ToString(CultureInfo.InvariantCulture),
                    importContent.PropertyStreamBytes.ToString(CultureInfo.InvariantCulture),
                    importContent.ResKeyTableBytes.ToString(CultureInfo.InvariantCulture),
                    importContent.ObjectIdTableBytes.ToString(CultureInfo.InvariantCulture),
                    importContent.Crc32.ToString("X8", CultureInfo.InvariantCulture),
                    importContent.EntryCount.ToString(CultureInfo.InvariantCulture),
                    importContent.ResKeyCount.ToString(CultureInfo.InvariantCulture),
                    importContent.ObjectIdCount.ToString(CultureInfo.InvariantCulture)),

            // request a deep household export from the local TS3.
            // The response metadata rides in the control frame while the heavy bytes are
            // published by the mod into the host BULK slot.
            BridgeCommand.ExportHouseholdContent exportContent =>
                Join(
                    "EXPORT_HOUSEHOLD_CONTENT",
                    Id(exportContent.CorrelationId),
                    exportContent.HouseholdId.ToString(CultureInfo.InvariantCulture)),

            // MERGE_TRAVELLING_HOUSEHOLD — reload-safe replacement.
            // Imports into a FRESH temp household, then merges into existing identities.
            // Wire: MERGE_TRAVELLING_HOUSEHOLD|<corr>|<householdId>|<totalBytes>|<crc32Hex>|<householdPropBytes>|<householdResKeyBytes>|<householdObjIdBytes>|<inventoryCount>
            BridgeCommand.MergeTravellingHousehold travelMerge =>
                Join(
                    "MERGE_TRAVELLING_HOUSEHOLD",
                    Id(travelMerge.CorrelationId),
                    travelMerge.HouseholdId.ToString(CultureInfo.InvariantCulture),
                    travelMerge.TotalBytes.ToString(CultureInfo.InvariantCulture),
                    travelMerge.Crc32.ToString("X8", CultureInfo.InvariantCulture),
                    travelMerge.HouseholdPropBytes.ToString(CultureInfo.InvariantCulture),
                    travelMerge.HouseholdResKeyBytes.ToString(CultureInfo.InvariantCulture),
                    travelMerge.HouseholdObjIdBytes.ToString(CultureInfo.InvariantCulture),
                    travelMerge.InventoryCount.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ApplyMotive applyMotive =>
                Join(
                    "APPLY_MOTIVE",
                    Id(applyMotive.CorrelationId),
                    applyMotive.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    Text(applyMotive.MotiveName),
                    Float(applyMotive.Value)),

            BridgeCommand.ProbeSimMotive probeMotive =>
                Join(
                    "PROBE_SIM_MOTIVE",
                    Id(probeMotive.CorrelationId),
                    probeMotive.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    Text(probeMotive.MotiveName)),

            BridgeCommand.ApplyInteractionQueue applyInteractionQueue =>
                EncodeApplyInteractionQueue(applyInteractionQueue),

            BridgeCommand.ApplyInteractions applyInteractions =>
                Join(
                    "APPLY_INTERACTIONS",
                    Id(applyInteractions.CorrelationId),
                    EncodeInteractionRequests(applyInteractions.Interactions)),

            BridgeCommand.ApplyForceAlign applyForceAlign =>
                EncodeApplyForceAlign(applyForceAlign),

            BridgeCommand.ProbeSimInteractionQueue probeQueue =>
                Join(
                    "PROBE_SIM_INTERACTION_QUEUE",
                    Id(probeQueue.CorrelationId),
                    probeQueue.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ApplyCash applyCash =>
                Join(
                    "APPLY_CASH",
                    Id(applyCash.CorrelationId),
                    applyCash.HouseholdId.ToString(CultureInfo.InvariantCulture),
                    applyCash.Funds.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ProbeHouseholdCash probeCash =>
                Join(
                    "PROBE_HOUSEHOLD_CASH",
                    Id(probeCash.CorrelationId),
                    probeCash.HouseholdId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ApplySkill applySkill =>
                Join(
                    "APPLY_SKILL",
                    Id(applySkill.CorrelationId),
                    applySkill.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    Text(applySkill.SkillName),
                    applySkill.Level.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ProbeSimSkill probeSkill =>
                Join(
                    "PROBE_SIM_SKILL",
                    Id(probeSkill.CorrelationId),
                    probeSkill.SimObjectId.ToString(CultureInfo.InvariantCulture),
Text(probeSkill.SkillName)),

            BridgeCommand.ApplyAutonomy applyAuto =>
                Join(
                    "APPLY_AUTONOMY",
                    Id(applyAuto.CorrelationId),
                    applyAuto.SimObjectId.ToString(CultureInfo.InvariantCulture),
                    applyAuto.Disabled ? "1" : "0"),

            BridgeCommand.ProbeSimAutonomy probeAuto =>
                Join(
                    "PROBE_SIM_AUTONOMY",
                    Id(probeAuto.CorrelationId),
                    probeAuto.SimObjectId.ToString(CultureInfo.InvariantCulture)),

            BridgeCommand.ShowNotification notify =>
                Join(
                    "NOTIFY",
                    Id(notify.CorrelationId),
                    Text(notify.Text)),

            _ => throw new NotSupportedException($"Unsupported bridge command {command.GetType().Name}.")
        };
    }

    /// <summary>
    /// Wire correlation ids are GUIDs in "N" format (32 lowercase hex digits, no dashes).
    /// Returns false for truncated or binary-corrupted ids — must never throw (reader loop safety).
    /// </summary>
    public static bool TryParseWireCorrelationId(string? raw, out Guid correlationId)
    {
        correlationId = Guid.Empty;
        if (string.IsNullOrEmpty(raw) || raw.Length != 32)
        {
            return false;
        }

        for (int i = 0; i < raw.Length; i++)
        {
            if (!Uri.IsHexDigit(raw[i]))
            {
                return false;
            }
        }

        return Guid.TryParseExact(raw, "N", out correlationId);
    }

    /// <summary>
    /// Reject host frames whose payload contains control bytes or malformed correlation ids.
    /// Used before consume/decode so corrupt memory reads cannot kill the reader loop.
    /// </summary>
    public static bool IsPlausibleHostFrameLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] < 32)
            {
                return false;
            }
        }

        int pipe = line.IndexOf('|');
        if (pipe <= 0)
        {
            return false;
        }

        string prefix = line[..pipe];
        if (prefix is "OK" or "ERROR" or "SESSION_READY" or "SNAPSHOT" or "SNAPSHOT_CHUNK" or "BULK_REF" or "BULK_NAK"
            or "IMPORT_HOUSEHOLD_CONTENT_REPORT")
        {
            string[] parts = line.Split('|');
            if (parts.Length >= 2 && !TryParseWireCorrelationId(parts[1], out _))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Try to decode a single bridge response line. Returns null for unknown/unparseable lines.
    /// Caller (HostBridgeServer) handles SNAPSHOT_CHUNK reassembly before invoking this on the assembled payload.
    /// </summary>
    public static BridgeResponse? TryDecodeResponse(string line)
    {
        line = line.TrimStart('\uFEFF');
        if (!IsPlausibleHostFrameLine(line))
        {
            return null;
        }

        string[] parts = line.Split('|');
        if (parts.Length == 0)
        {
            return null;
        }

        if (parts[0] == "OK" && parts.Length >= 2)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }

            return BridgeResponse.Accepted(correlationId);
        }

        if (parts[0] == "ERROR" && parts.Length >= 3)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }

            return BridgeResponse.Rejected(correlationId, DecodeText(parts[2]));
        }

        if (parts[0] == "SESSION_READY" && parts.Length >= 5)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string saveName = DecodeText(parts[2]);
            string worldName = DecodeText(parts[3]);
            long tick = long.Parse(parts[4], CultureInfo.InvariantCulture);
            SimulationSnapshot snapshot = new(tick, worldName, saveName, GameSpeed.Pause, Array.Empty<SimSnapshot>(), Array.Empty<ObjectSnapshot>());
            return BridgeResponse.Accepted(correlationId, snapshot);
        }

        if (parts[0] == "SNAPSHOT" && parts.Length >= 6)
        {
            try
            {
                return DecodeSnapshot(parts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bridge] DecodeSnapshot failed: {ex}");
                return null;
            }
        }

        if (parts[0] == "AUTONOMY_PROBE" && parts.Length >= 4)
        {
            // Accepted carries the probe report in RejectionReason field as a quick channel (no schema change to BridgeResponse).
            // Layout: AUTONOMY_PROBE|<corrId>|<resultKind: ok|partial|none>|<hex strategy-report>
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string strategy = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(strategy);
            }
            return BridgeResponse.Rejected(correlationId, $"autonomy-probe={kind}: {strategy}");
        }

        if (parts[0] == "SIM_LIFECYCLE_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"sim-lifecycle-probe={kind}: {report}");
        }

        if (parts[0] == "ACTOR_LOOKUP_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"actor-lookup-probe={kind}: {report}");
        }

        if (parts[0] == "SIM_MOTIVE_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"sim-motive-probe={kind}: {report}");
        }

        if (parts[0] == "SIM_INTERACTION_QUEUE_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"sim-interaction-queue-probe={kind}: {report}");
        }

        if (parts[0] == "HOUSEHOLD_CASH_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"household-cash-probe={kind}: {report}");
        }

        if (parts[0] == "SIM_SKILL_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"sim-skill-probe={kind}: {report}");
        }

        if (parts[0] == "SIM_AUTONOMY_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"sim-autonomy-probe={kind}: {report}");
        }

        if (parts[0] == "SIM_STATUS_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"sim-status-probe={kind}: {report}");
        }

        if ((parts[0] == "DIAG_STATE_REPORT" || parts[0] == "DIAG_QUEUE_REPORT" || parts[0] == "DIAG_INTERACTION_BUNDLE_REPORT" || parts[0] == "DIAG_INTERACTION_STATE_REPORT" || parts[0] == "DIAG_RESOLVE_REPORT" || parts[0] == "DIAG_WALK_REPORT"
                || parts[0] == "DIAG_BLOB_REPORT" || parts[0] == "DIAG_LOADTEST_REPORT" || parts[0] == "DIAG_RUN_REPORT") && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"{parts[0]}={kind}: {report}");
        }

        if (parts[0] == "STATE_INJECTION_PROBE" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }

            return BridgeResponse.Rejected(correlationId, $"state-injection-probe={kind}: {report}");
        }

        // deep import report. The mod runs Household.ImportContent + PostImport
        // fixups and reports ok/error + a summary (remapped SimDescriptionIds, entry count verified).
        // Same NAME|corrId|kind|hexReport shape as the other probe reports.
        if (parts[0] == "IMPORT_HOUSEHOLD_CONTENT_REPORT" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }
            return BridgeResponse.Rejected(correlationId, $"household-import={kind}: {report}");
        }

        // travel-merge report. Same NAME|corrId|kind|hexReport shape.
        if (parts[0] == "MERGE_TRAVELLING_HOUSEHOLD_REPORT" && parts.Length >= 4)
        {
            if (!TryParseWireCorrelationId(parts[1], out Guid correlationId))
            {
                return null;
            }
            string kind = parts[2];
            string report = DecodeText(parts[3]);
            if (kind == "ok")
            {
                return BridgeResponse.Accepted(correlationId).WithProbeReport(report);
            }
            return BridgeResponse.Rejected(correlationId, $"travel-merge={kind}: {report}");
        }

        return null;
    }

    /// <summary>
    /// Decode the hex-wrapped DIAG_BLOB_REPORT body emitted by EXPORT_HOUSEHOLD_CONTENT.
    /// The report is a strict semicolon-delimited key/value set; payload bytes never ride here.
    /// </summary>
    public static HouseholdExportMetadataDecodeResult DecodeHouseholdExportMetadata(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return HouseholdExportMetadataDecodeResult.Failure(
                HouseholdExportTransportError.MetadataMissing,
                "Household export response did not contain metadata.");
        }

        string[] expectedKeys =
        [
            "exported",
            "householdId",
            "slotIndex",
            "totalBytes",
            "propertyStreamBytes",
            "resKeyTableBytes",
            "objectIdTableBytes",
            "crc32",
            "entryCount",
            "resKeyCount",
            "objectIdCount",
        ];
        // inventoryCount and inventorySegments are optional v2 fields.
        HashSet<string> expected = new(expectedKeys, StringComparer.Ordinal);
        expected.Add("inventoryCount");
        expected.Add("inventorySegments");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string field in report.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = field.IndexOf('=');
            if (equals <= 0 || equals == field.Length - 1)
            {
                return HouseholdExportMetadataDecodeResult.Failure(
                    HouseholdExportTransportError.MetadataMalformed,
                    $"Malformed household export metadata field '{field}'.");
            }

            string key = field[..equals];
            if (!expected.Contains(key))
            {
                return HouseholdExportMetadataDecodeResult.Failure(
                    HouseholdExportTransportError.MetadataUnknownField,
                    $"Unknown household export metadata field '{key}'.");
            }

            if (!values.TryAdd(key, field[(equals + 1)..]))
            {
                return HouseholdExportMetadataDecodeResult.Failure(
                    HouseholdExportTransportError.MetadataDuplicateField,
                    $"Duplicate household export metadata field '{key}'.");
            }
        }

        foreach (string key in expectedKeys)
        {
            if (!values.ContainsKey(key))
            {
                return HouseholdExportMetadataDecodeResult.Failure(
                    HouseholdExportTransportError.MetadataMissingField,
                    $"Household export metadata is missing '{key}'.");
            }
        }

        if (!bool.TryParse(values["exported"], out bool exported)
            || !exported
            || !ulong.TryParse(values["householdId"], NumberStyles.None, CultureInfo.InvariantCulture, out ulong householdId)
            || !int.TryParse(values["slotIndex"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slotIndex)
            || !int.TryParse(values["totalBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int totalBytes)
            || !int.TryParse(values["propertyStreamBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int propertyStreamBytes)
            || !int.TryParse(values["resKeyTableBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int resKeyTableBytes)
            || !int.TryParse(values["objectIdTableBytes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int objectIdTableBytes)
            || !uint.TryParse(values["crc32"], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc32)
            || !int.TryParse(values["entryCount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int entryCount)
            || !int.TryParse(values["resKeyCount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int resKeyCount)
            || !int.TryParse(values["objectIdCount"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int objectIdCount))
        {
            return HouseholdExportMetadataDecodeResult.Failure(
                HouseholdExportTransportError.MetadataInvalidValue,
                "Household export metadata contains a non-numeric value.");
        }

        int inventoryCount = 0;
        if (values.TryGetValue("inventoryCount", out string? invCountStr))
        {
            int.TryParse(invCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out inventoryCount);
        }
        string? inventorySegments = values.TryGetValue("inventorySegments", out string? seg) ? seg : null;

        return HouseholdExportMetadataDecodeResult.Success(new HouseholdExportMetadata(
            householdId,
            slotIndex,
            totalBytes,
            propertyStreamBytes,
            resKeyTableBytes,
            objectIdTableBytes,
            crc32,
            entryCount,
            resKeyCount,
            objectIdCount,
            inventoryCount,
            inventorySegments));
    }

    /// <summary>
    /// Decode a SNAPSHOT line where snapshot body may include Sim and Object item arrays.
    /// Format: SNAPSHOT|corrId|tick|hexWorld|hexSave|speed|simCount|sim0|...|objCount|obj0|...
    /// Each sim item is colon-delimited (objectId:hexName:householdId:x:y:z:interactionId|-:hexInteractionKey|-:owningPlayerId|-:lotId|-:interactionQueueCount|-:simDescriptionId|-)
    /// Each obj item is colon-delimited (objectId:hexClass:x:y:z:lotId|-).
    /// </summary>
    public static BridgeResponse? DecodeSnapshot(string[] parts)
    {
        Guid correlationId = Guid.Parse(parts[1]);
        long tick = long.Parse(parts[2], CultureInfo.InvariantCulture);
        string worldName = DecodeText(parts[3]);
        string saveName = DecodeText(parts[4]);
        GameSpeed speed = DecodeSpeed(parts[5]);

        List<SimSnapshot> sims = new();
        List<ObjectSnapshot> objects = new();

        int idx = 6;
        if (idx < parts.Length)
        {
            int simCount = int.Parse(parts[idx++], CultureInfo.InvariantCulture);
            for (int i = 0; i < simCount && idx < parts.Length; i++, idx++)
            {
                SimSnapshot? s = DecodeSimItem(parts[idx]);
                if (s != null)
                {
                    sims.Add(s);
                }
            }

            if (idx < parts.Length)
            {
                int objCount = int.Parse(parts[idx++], CultureInfo.InvariantCulture);
                for (int i = 0; i < objCount && idx < parts.Length; i++, idx++)
                {
                    ObjectSnapshot? o = DecodeObjectItem(parts[idx]);
                    if (o != null)
                    {
                        objects.Add(o);
                    }
                }
            }
        }

        SimulationSnapshot snapshot = new(tick, worldName, saveName, speed, sims, objects);
        return BridgeResponse.Accepted(correlationId, snapshot);
    }

    private static string FormatSimAliases(IReadOnlyList<SimAlias> aliases)
        => SimsOnlineServerHost.SimAliasWire.Format(
            aliases.Select(a => a.ServerObjectId).ToList(),
            aliases.Select(a => a.SimDescriptionId).ToList());

    private static SimSnapshot? DecodeSimItem(string item)
    {
        string[] f = item.Split(':');
        if (f.Length < 8) return null;
        try
        {
            ulong objectId = ulong.Parse(f[0], CultureInfo.InvariantCulture);
            string name = DecodeText(f[1]);
            ulong household = ulong.Parse(f[2], CultureInfo.InvariantCulture);
            float x = float.Parse(f[3], CultureInfo.InvariantCulture);
            float y = float.Parse(f[4], CultureInfo.InvariantCulture);
            float z = float.Parse(f[5], CultureInfo.InvariantCulture);
            ulong? currentInteraction = f[6] == "-" ? null : ulong.Parse(f[6], CultureInfo.InvariantCulture);
            string? currentInteractionKey;
            int? owningPlayerId;
            ulong? lotId = null;
            int? interactionQueueCount = null;
            ulong? simDescriptionId = null;
            if (f.Length >= 9)
            {
                currentInteractionKey = f[7] == "-" ? null : DecodeText(f[7]);
                owningPlayerId = f[8] == "-" ? null : int.Parse(f[8], CultureInfo.InvariantCulture);
                if (f.Length >= 10 && f[9] != "-")
                {
                    lotId = ulong.Parse(f[9], CultureInfo.InvariantCulture);
                }

                if (f.Length >= 11 && f[10] != "-")
                {
                    interactionQueueCount = int.Parse(f[10], CultureInfo.InvariantCulture);
                }

                if (f.Length >= 12 && f[11] != "-")
                {
                    simDescriptionId = ulong.Parse(f[11], CultureInfo.InvariantCulture);
                }
            }
            else
            {
                currentInteractionKey = null;
                owningPlayerId = f[7] == "-" ? null : int.Parse(f[7], CultureInfo.InvariantCulture);
            }

            return new SimSnapshot(objectId, name, household, new Vector3Dto(x, y, z), currentInteraction, owningPlayerId, currentInteractionKey, lotId, interactionQueueCount, SimDescriptionId: simDescriptionId);
        }
        catch
        {
            return null;
        }
    }

    private static ObjectSnapshot? DecodeObjectItem(string item)
    {
        string[] f = item.Split(':');
        if (f.Length < 6) return null;
        try
        {
            ulong objectId = ulong.Parse(f[0], CultureInfo.InvariantCulture);
            string scriptClass = DecodeText(f[1]);
            float x = float.Parse(f[2], CultureInfo.InvariantCulture);
            float y = float.Parse(f[3], CultureInfo.InvariantCulture);
            float z = float.Parse(f[4], CultureInfo.InvariantCulture);
            ulong? lotId = f[5] == "-" ? null : ulong.Parse(f[5], CultureInfo.InvariantCulture);
            return new ObjectSnapshot(objectId, scriptClass, new Vector3Dto(x, y, z), lotId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decode an EVENT line. Format: EVENT|eventKey|payload-fields...
    /// Returns the event record on success; null if unknown event key. A line of a known kind that does not decode
    /// is null too, and is counted (and the first of its kind logged) in <see cref="BridgeEventDrops.Shared"/>.
    /// </summary>
    public static ServerEvent? TryDecodeEvent(string line) => TryDecodeEvent(line, BridgeEventDrops.Shared);

    /// <summary><see cref="TryDecodeEvent(string)"/>, counting drops in <paramref name="drops"/>.</summary>
    public static ServerEvent? TryDecodeEvent(string line, BridgeEventDrops drops)
    {
        line = line.TrimStart('\uFEFF');
        string[] parts = line.Split('|');
        if (parts.Length < 2 || parts[0] != "EVENT")
        {
            return null;
        }

        string key = parts[1];
        string reason;
        try
        {
            // Every arm's guard is HasFields: a short line of a kind decoded here throws, and is a drop like a field that
            // does not parse; only a kind with no arm is a plain null.
            return key switch
            {
                "InteractionQueued" when HasFields(parts, 10) =>
                    new ServerEvent.InteractionQueued(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture),
                        DecodeText(parts[4]),
                        string.IsNullOrEmpty(parts[5]) ? null : ulong.Parse(parts[5], CultureInfo.InvariantCulture),
                        parts[6] == "1"
                            ? new Vector3Dto(
                                float.Parse(parts[7], CultureInfo.InvariantCulture),
                                float.Parse(parts[8], CultureInfo.InvariantCulture),
                                float.Parse(parts[9], CultureInfo.InvariantCulture))
                            : (Vector3Dto?)null,
                        DecodeParameterTail(parts.Length >= 11 ? parts[10] : null)),

                "InteractionReplayQueued" when HasFields(parts, 3) =>
                    DecodeInteractionReplayQueued(parts[2]),

                "InteractionStarted" when HasFields(parts, 4) =>
                    new ServerEvent.InteractionStarted(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture)),

                "InteractionCompleted" when HasFields(parts, 4) =>
                    new ServerEvent.InteractionCompleted(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture)),

                "InteractionFailed" when HasFields(parts, 5) =>
                    new ServerEvent.InteractionFailed(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture),
                        DecodeText(parts[4])),

                "IntentCaptured" when HasFields(parts, 9) =>
                    new ServerEvent.IntentCaptured(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture),
                        DecodeText(parts[4]),
                        parts[5] == "1"
                            ? new Vector3Dto(
                                float.Parse(parts[6], CultureInfo.InvariantCulture),
                                float.Parse(parts[7], CultureInfo.InvariantCulture),
                                float.Parse(parts[8], CultureInfo.InvariantCulture))
                            : (Vector3Dto?)null),

                // input forwarding: EVENT|UserIntent|actor|target|hexType|hexName|hexPath|hit|x|y|z|picked
                "UserIntent" when HasFields(parts, 12) =>
                    new ServerEvent.UserIntentCaptured(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture),
                        DecodeText(parts[4]),
                        DecodeText(parts[5]),
                        DecodeText(parts[6]),
                        parts[7],
                        parts[8] == "-"
                            ? (Vector3Dto?)null
                            : new Vector3Dto(
                                float.Parse(parts[8], CultureInfo.InvariantCulture),
                                float.Parse(parts[9], CultureInfo.InvariantCulture),
                                float.Parse(parts[10], CultureInfo.InvariantCulture)),
                        parts[11]),

                // EVENT|DialogRequest|id|sim|hexKind|argCount|hexArg...
                "DialogRequest" when HasFields(parts, 6) =>
                    new ServerEvent.DialogRequested(
                        int.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture),
                        DecodeText(parts[4]),
                        Enumerable.Range(0, int.Parse(parts[5], CultureInfo.InvariantCulture))
                            .Select(i => 6 + i < parts.Length ? DecodeText(parts[6 + i]) : string.Empty)
                            .ToArray()),

                // EVENT|DialogAnswer|id|sim|hexAnswer ("-" = none)
                "DialogAnswer" when HasFields(parts, 5) =>
                    new ServerEvent.DialogAnswerCaptured(
                        int.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture),
                        parts[4] == "-" ? null : DecodeText(parts[4])),

                // EVENT|PickFailed|commandId|actor|interactionId|hexReason. A player's pick the server's
                // queue accepted and then dropped (reason ObjectInUse, RouteFailed, FailedToStart or NotRun); the
                // listener sends it to the player who made the pick, as the CommandRejected of that command.
                "PickFailed" when HasFields(parts, 6) =>
                    new ServerEvent.CommandRejected(Guid.Parse(parts[2]), DecodeText(parts[5])),

                // queue mirror: EVENT|CancelIntent|actor|interactionId
                "CancelIntent" when HasFields(parts, 4) =>
                    new ServerEvent.CancelIntentCaptured(
                        ulong.Parse(parts[2], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[3], CultureInfo.InvariantCulture)),

                // n14: EVENT|LeaveConversationIntent|actor (the server's id)
                "LeaveConversationIntent" when HasFields(parts, 3) =>
                    new ServerEvent.LeaveConversationIntentCaptured(ulong.Parse(parts[2], CultureInfo.InvariantCulture)),

                "SpeedIntentCaptured" when HasFields(parts, 3) =>
                    new ServerEvent.SpeedIntentCaptured(DecodeSpeed(parts[2])),

                "ServerError" when HasFields(parts, 3) =>
                    new ServerEvent.ServerError(DecodeText(parts[2])),

                _ => null
            };
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
        }

        drops.Note(key, reason, line);
        return null;
    }

    // A TryDecodeEvent guard: true when the line has the fields its kind needs, else it throws.
    private static bool HasFields(string[] parts, int count) =>
        parts.Length >= count ? true : throw new FormatException($"too few fields ({parts.Length} of {count})");

    private static ServerEvent DecodeInteractionReplayQueued(string encodedReport)
    {
        string report = DecodeText(encodedReport);
        if (!InteractionReplayBundleParser.TryParseFromCaptureReport(report, out InteractionReplayBundle? bundle, out string? error)
            || bundle == null)
        {
            throw new FormatException("capture report: " + (error ?? "no bundle"));
        }

        return new ServerEvent.InteractionReplayQueued(bundle);
    }

    public static bool IsEvent(string line) =>
        line.TrimStart('\uFEFF').StartsWith("EVENT|", StringComparison.Ordinal);

    public static bool IsSnapshotChunk(string line) =>
        line.TrimStart('\uFEFF').StartsWith("SNAPSHOT_CHUNK|", StringComparison.Ordinal);

    public static bool IsBulkRef(string line) =>
        line.TrimStart('\uFEFF').StartsWith("BULK_REF|", StringComparison.Ordinal);

    /// <summary>
    /// BULK_REF|<corrId>|snapshot|slot|totalLength|crc32Hex
    /// BULK_REF|<corrId>|snapshot-part|slot|partIndex|partCount|totalLength|crc32Hex
    /// </summary>
    public static BulkRefParseResult? TryParseBulkRef(string line)
    {
        line = line.TrimStart('\uFEFF');
        string[] parts = line.Split('|');
        if (parts.Length < 6 || parts[0] != "BULK_REF")
        {
            return null;
        }

        try
        {
            Guid correlationId = Guid.Parse(parts[1]);
            string kind = parts[2];
            int slotIndex = int.Parse(parts[3], CultureInfo.InvariantCulture);
            if (kind == "snapshot-part")
            {
                if (parts.Length < 8)
                {
                    return null;
                }

                int partIndex = int.Parse(parts[4], CultureInfo.InvariantCulture);
                int partCount = int.Parse(parts[5], CultureInfo.InvariantCulture);
                int totalLength = int.Parse(parts[6], CultureInfo.InvariantCulture);
                uint crc32 = uint.Parse(parts[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new BulkRefParseResult(correlationId, kind, slotIndex, totalLength, crc32, partIndex, partCount);
            }

            int length = int.Parse(parts[4], CultureInfo.InvariantCulture);
            uint crc = uint.Parse(parts[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new BulkRefParseResult(correlationId, kind, slotIndex, length, crc, null, null);
        }
        catch
        {
            return null;
        }
    }

    public readonly record struct BulkRefParseResult(
        Guid CorrelationId,
        string Kind,
        int SlotIndex,
        int TotalLength,
        uint Crc32,
        int? PartIndex,
        int? PartCount);

    /// <summary>
    /// Decode a SNAPSHOT_CHUNK line into its components.
    /// Format: SNAPSHOT_CHUNK|<corrId>|<chunkIndex>|<chunkCount>|<raw-chunk-payload>
    /// Returns (corrId, index, count, payload) or null on parse failure.
    /// Payload here is the raw chunk content; assembly happens in HostBridgeServer.
    /// </summary>
    public static (Guid CorrelationId, int Index, int Count, string Payload)? TryDecodeChunk(string line)
    {
        line = line.TrimStart('\uFEFF');
        string[] parts = line.Split('|', 5);
        if (parts.Length < 5 || parts[0] != "SNAPSHOT_CHUNK")
        {
            return null;
        }

        try
        {
            Guid corrId = Guid.Parse(parts[1]);
            int idx = int.Parse(parts[2], CultureInfo.InvariantCulture);
            int count = int.Parse(parts[3], CultureInfo.InvariantCulture);
            return (corrId, idx, count, parts[4]);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsHello(string line)
    {
        line = line.TrimStart('\uFEFF');
        return line.StartsWith("HELLO|", StringComparison.Ordinal);
    }

    /// <summary>
    /// CONNECT|19 must yield HELLO|6+ (caps). Stale HELLO|5 frames from a prior v10
    /// session must not satisfy a v11 re-attach handshake.
    /// </summary>
    public static bool IsConnectHandshakeHello(string line)
    {
        return TryParseHello(line, out HelloParseResult hello) && hello.WireVersion >= 6;
    }

    /// <summary>parse HELLO v4 (no stamp) and v5 (hex build stamp).</summary>
    public static bool TryParseHello(string line, out HelloParseResult result)
    {
        result = default;
        line = line.TrimStart('\uFEFF');
        if (!line.StartsWith("HELLO|", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = line.Split('|');
        if (parts.Length < 4)
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int wireVersion))
        {
            return false;
        }

        if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long tick))
        {
            return false;
        }

        string? buildStamp = parts.Length >= 5 ? DecodeText(parts[4]) : null;
        string? caps = parts.Length >= 6 ? DecodeText(parts[5]) : null;
        result = new HelloParseResult(DecodeText(parts[2]), tick, buildStamp, wireVersion, caps);
        return true;
    }

    /// <summary>Extract git short-sha from a HELLO build stamp ({sha}@{utc}).</summary>
    public static string? GitShaFromBuildStamp(string? buildStamp)
    {
        if (string.IsNullOrWhiteSpace(buildStamp))
        {
            return null;
        }

        int at = buildStamp.IndexOf('@');
        return at <= 0 ? buildStamp : buildStamp[..at];
    }

    public readonly record struct HelloParseResult(string WorldName, long Tick, string? BuildStamp, int WireVersion, string? Caps);

    /// <summary>
    /// HELLO|6 caps negotiate v11 ctl routing when the compat-slot HELLO stomps bytes 32-63
    /// before EnsureControlHeader runs on the mod pump (fresh client TS3 boot).
    /// </summary>
    public static bool CapsIndicateLayoutV11(string? caps)
    {
        if (string.IsNullOrWhiteSpace(caps))
        {
            return false;
        }

        foreach (string segment in caps.Split(','))
        {
            string part = segment.Trim();
            if (part.StartsWith("layout=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part.AsSpan("layout=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int layoutVersion)
                && layoutVersion >= BridgeSharedRegionLayout.LayoutVersion)
            {
                return true;
            }

            if (part.StartsWith("multi-slot=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string EncodeQueueInteraction(BridgeCommand.QueueInteraction command)
    {
        InteractionRequest interaction = command.Interaction;
        string targetObjectId = interaction.TargetObjectId?.ToString(CultureInfo.InvariantCulture) ?? "";
        string hasPosition = interaction.TargetPosition.HasValue ? "1" : "0";
        Vector3Dto position = interaction.TargetPosition.GetValueOrDefault();

        if (UserPick.IsUserPick(interaction))
        {
            // a player's pick -- the server mod resolves it like the pie menu (13 fields).
            IReadOnlyDictionary<string, string> pick = interaction.Parameters;
            return Join(
                "QUEUE_INTERACTION",
                Id(command.CorrelationId),
                interaction.ActorObjectId.ToString(CultureInfo.InvariantCulture),
                Text(interaction.InteractionKey),
                targetObjectId,
                hasPosition,
                Float(position.X),
                Float(position.Y),
                Float(position.Z),
                Text(pick.GetValueOrDefault(UserPick.Name, string.Empty)),
                Text(pick.GetValueOrDefault(UserPick.Path, string.Empty)),
                pick.GetValueOrDefault(UserPick.Hit, SimsOnlineServerHost.UserIntentWire.FormatHit(0, 0uL, 0u)),
                pick.GetValueOrDefault(UserPick.Picked, SimsOnlineServerHost.UserIntentWire.Empty));
        }

        return Join(
            "QUEUE_INTERACTION",
            Id(command.CorrelationId),
            interaction.ActorObjectId.ToString(CultureInfo.InvariantCulture),
            Text(interaction.InteractionKey),
            targetObjectId,
            hasPosition,
            Float(position.X),
            Float(position.Y),
            Float(position.Z));
    }

    private static string EncodeApplyForceAlign(BridgeCommand.ApplyForceAlign command)
    {
        InteractionReplayBundle bundle = command.Bundle;
        string postureContainerId = bundle.PostureContainerObjectId?.ToString(CultureInfo.InvariantCulture) ?? "0";
        string isTransition = bundle.IsTransitionInteraction ? "1" : "0";
        string parameters = EncodeForceAlignParameters(bundle.Parameters);
        if (parameters.Length == 0)
        {
            return Join(
                "APPLY_FORCE_ALIGN",
                Id(command.CorrelationId),
                bundle.ActorObjectId.ToString(CultureInfo.InvariantCulture),
                Text(bundle.InteractionKey),
                postureContainerId,
                isTransition);
        }

        return Join(
            "APPLY_FORCE_ALIGN",
            Id(command.CorrelationId),
            bundle.ActorObjectId.ToString(CultureInfo.InvariantCulture),
            Text(bundle.InteractionKey),
            postureContainerId,
            isTransition,
            Text(parameters));
    }

    private static string EncodeForceAlignParameters(IReadOnlyDictionary<string, string> parameters) =>
        EncodeSelectedParameters(parameters, ForceAlignParameterWireKeys);

    private static string EncodeSelectedParameters(IReadOnlyDictionary<string, string> parameters, IReadOnlyList<string> wireKeys)
    {
        if (parameters.Count == 0)
        {
            return "";
        }

        StringBuilder builder = new();
        foreach (string key in wireKeys)
        {
            if (!parameters.TryGetValue(key, out string? value) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(key);
            builder.Append('=');
            builder.Append(value);
        }

        return builder.ToString();
    }

    private static string EncodeApplyInteractionQueue(BridgeCommand.ApplyInteractionQueue command)
    {
        InteractionRequest interaction = command.Interaction;
        string targetObjectId = interaction.TargetObjectId?.ToString(CultureInfo.InvariantCulture) ?? "";
        string hasPosition = interaction.TargetPosition.HasValue ? "1" : "0";
        Vector3Dto position = interaction.TargetPosition.GetValueOrDefault();
        string parameters = EncodeInteractionParameters(interaction.Parameters);
        if (parameters.Length == 0)
        {
            return Join(
                "APPLY_INTERACTION_QUEUE",
                Id(command.CorrelationId),
                interaction.ActorObjectId.ToString(CultureInfo.InvariantCulture),
                Text(interaction.InteractionKey),
                targetObjectId,
                hasPosition,
                Float(position.X),
                Float(position.Y),
                Float(position.Z));
        }

        return Join(
            "APPLY_INTERACTION_QUEUE",
            Id(command.CorrelationId),
            interaction.ActorObjectId.ToString(CultureInfo.InvariantCulture),
            Text(interaction.InteractionKey),
            targetObjectId,
            hasPosition,
            Float(position.X),
            Float(position.Y),
            Float(position.Z),
            Text(parameters));
    }

    private static string EncodeInteractionParameters(IReadOnlyDictionary<string, string> parameters) =>
        EncodeSelectedParameters(parameters, InteractionParameterWireKeys);       
    
    /// <summary>
    /// BQ: decode the optional parameter tail on an emitted InteractionQueued event line
    /// (comma-joined key=value pairs from the BQ key catalog; both wire-key allowlists are
    /// accepted so seat keys ride the same tail). Absent or empty tail decodes to null.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? DecodeParameterTail(string? tail)
    {
        if (string.IsNullOrEmpty(tail))
        {
            return null;
        }
    
        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        foreach (string pair in tail.Split(','))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }
    
            string key = pair[..separator];
            if (Array.IndexOf(InteractionParameterWireKeys, key) < 0
                && Array.IndexOf(ForceAlignParameterWireKeys, key) < 0)
            {
                continue;
            }
    
            parameters[key] = pair[(separator + 1)..];
        }
    
        return parameters.Count > 0 ? parameters : null;
    }

    private static string EncodeInjectReadIntent(BridgeCommand.InjectReadIntent command)
    {
        string parameters = command.Parameters == null
            ? ""
            : EncodeInteractionParameters(command.Parameters);
        if (parameters.Length == 0)
        {
            return Join(
                "INJECT_READ_INTENT",
                Id(command.CorrelationId),
                command.SimObjectId.ToString(CultureInfo.InvariantCulture),
                command.BookObjectId.ToString(CultureInfo.InvariantCulture),
                Text(command.Mode));
        }

        return Join(
            "INJECT_READ_INTENT",
            Id(command.CorrelationId),
            command.SimObjectId.ToString(CultureInfo.InvariantCulture),
            command.BookObjectId.ToString(CultureInfo.InvariantCulture),
            Text(command.Mode),
            Text(parameters));
    }

    /// <summary>
    /// split a flat list of (simId, position) pairs into one or more
    /// <see cref="BridgeCommand.ApplyPositions"/> commands, each of whose encoded form fits within
    /// <see cref="ApplyPositionsFrameBudgetBytes"/> (the single-frame transport limit). A ~71-Sim
    /// snapshot (~50 B/Sim) fits in one or two frames. Each chunk gets a fresh correlation id — it
    /// is an independent bridge round-trip with its own OK. A single pair that alone exceeds the
    /// budget is still emitted as its own one-pair command (it would only overflow with absurd
    /// coordinates; preserving the round-trip beats silently dropping the Sim).
    /// </summary>
    public static IReadOnlyList<BridgeCommand.ApplyPositions> ChunkApplyPositions(
        IReadOnlyList<(ulong SimObjectId, Vector3Dto Position)> positions)
    {
        List<BridgeCommand.ApplyPositions> chunks = new();
        if (positions == null || positions.Count == 0)
        {
            return chunks;
        }

        // Fixed per-frame overhead: "APPLY_POSITIONS" + "|" + 32-char corr (N format) + "|".
        int prefixLength = "APPLY_POSITIONS".Length + 1 + 32 + 1;
        int payloadBudget = ApplyPositionsFrameBudgetBytes - prefixLength;

        List<(ulong SimObjectId, Vector3Dto Position)> current = new();
        int currentLength = 0;   // encoded length of the pairs portion accumulated for this chunk
        foreach ((ulong SimObjectId, Vector3Dto Position) pair in positions)
        {
            string encoded = EncodePositionPair(pair.SimObjectId, pair.Position);
            // +1 for the ';' separator unless this would be the first pair in the chunk.
            int addedLength = current.Count == 0 ? encoded.Length : encoded.Length + 1;
            if (current.Count > 0 && currentLength + addedLength > payloadBudget)
            {
                chunks.Add(new BridgeCommand.ApplyPositions(Guid.NewGuid(), current));
                current = new List<(ulong SimObjectId, Vector3Dto Position)>();
                currentLength = 0;
                addedLength = encoded.Length;
            }

            current.Add(pair);
            currentLength += addedLength;
        }

        if (current.Count > 0)
        {
            chunks.Add(new BridgeCommand.ApplyPositions(Guid.NewGuid(), current));
        }

        return chunks;
    }

    /// <summary>
    /// split interaction replay requests into one or more
    /// <see cref="BridgeCommand.ApplyInteractions"/> commands within the frame budget.
    /// </summary>
    public static IReadOnlyList<BridgeCommand.ApplyInteractions> ChunkApplyInteractions(
        IReadOnlyList<InteractionRequest> interactions)
    {
        List<BridgeCommand.ApplyInteractions> chunks = new();
        if (interactions == null || interactions.Count == 0)
        {
            return chunks;
        }

        int prefixLength = "APPLY_INTERACTIONS".Length + 1 + 32 + 1;
        int payloadBudget = ApplyInteractionsFrameBudgetBytes - prefixLength;

        List<InteractionRequest> current = new();
        int currentLength = 0;
        foreach (InteractionRequest interaction in interactions)
        {
            string encoded = EncodeInteractionRequestPair(interaction);
            int addedLength = current.Count == 0 ? encoded.Length : encoded.Length + 1;
            if (current.Count > 0 && currentLength + addedLength > payloadBudget)
            {
                chunks.Add(new BridgeCommand.ApplyInteractions(Guid.NewGuid(), current));
                current = new List<InteractionRequest>();
                currentLength = 0;
                addedLength = encoded.Length;
            }

            current.Add(interaction);
            currentLength += addedLength;
        }

        if (current.Count > 0)
        {
            chunks.Add(new BridgeCommand.ApplyInteractions(Guid.NewGuid(), current));
        }

        return chunks;
    }

    private static string EncodeInteractionRequests(IReadOnlyList<InteractionRequest> interactions)
    {
        StringBuilder builder = new();
        for (int i = 0; i < interactions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            builder.Append(EncodeInteractionRequestPair(interactions[i]));
        }

        return builder.ToString();
    }

    private static string EncodeInteractionRequestPair(InteractionRequest interaction)
    {
        string targetObjectId = interaction.TargetObjectId?.ToString(CultureInfo.InvariantCulture) ?? "";
        string hasPosition = interaction.TargetPosition.HasValue ? "1" : "0";
        Vector3Dto position = interaction.TargetPosition.GetValueOrDefault();
        string pair = interaction.ActorObjectId.ToString(CultureInfo.InvariantCulture) + ":" +
            Text(interaction.InteractionKey) + ":" +
            targetObjectId + ":" +
            hasPosition + ":" +
            Float(position.X) + ":" +
            Float(position.Y) + ":" +
            Float(position.Z);
        string parameters = EncodeInteractionParameters(interaction.Parameters);
        if (parameters.Length == 0)
        {
            return pair;
        }

        return pair + ":" + Text(parameters);
    }

    private static string EncodePositionPairs(IReadOnlyList<(ulong SimObjectId, Vector3Dto Position)> positions)
    {
        StringBuilder builder = new();
        for (int i = 0; i < positions.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            builder.Append(EncodePositionPair(positions[i].SimObjectId, positions[i].Position));
        }

        return builder.ToString();
    }

    private static string EncodePositionPair(ulong simObjectId, Vector3Dto position) =>
        simObjectId.ToString(CultureInfo.InvariantCulture) + ":" +
        Float(position.X) + ":" + Float(position.Y) + ":" + Float(position.Z);

    private static string EncodeLotSimExports(IReadOnlyList<LotSimExport> sims)
    {
        StringBuilder builder = new();
        for (int i = 0; i < sims.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            builder.Append(EncodePositionPair(sims[i].ObjectId, sims[i].Position));
        }

        return builder.ToString();
    }

    /// <summary>
    /// slice 6: split a lot export bundle into one or more
    /// <see cref="BridgeCommand.MergeLotState"/> commands whose encoded form fits the
    /// single-frame transport budget (mirrors <see cref="ChunkApplyPositions"/>). Each chunk is
    /// an independent bridge round-trip with its own OK; the lot id repeats on every chunk.
    /// </summary>
    public static IReadOnlyList<BridgeCommand.MergeLotState> ChunkMergeLotState(LotExportBundle bundle)
    {
        List<BridgeCommand.MergeLotState> chunks = new();
        if (bundle == null || bundle.Sims.Count == 0)
        {
            return chunks;
        }

        string lotId = bundle.LotId.ToString(CultureInfo.InvariantCulture);
        int prefixLength = "MERGE_LOT_STATE".Length + 1 + 32 + 1 + lotId.Length + 1;
        int payloadBudget = ApplyPositionsFrameBudgetBytes - prefixLength;

        List<LotSimExport> current = new();
        int currentLength = 0;
        foreach (LotSimExport sim in bundle.Sims)
        {
            string encoded = EncodePositionPair(sim.ObjectId, sim.Position);
            int addedLength = current.Count == 0 ? encoded.Length : encoded.Length + 1;
            if (current.Count > 0 && currentLength + addedLength > payloadBudget)
            {
                chunks.Add(new BridgeCommand.MergeLotState(Guid.NewGuid(), bundle with { Sims = current }));
                current = new List<LotSimExport>();
                currentLength = 0;
                addedLength = encoded.Length;
            }

            current.Add(sim);
            currentLength += addedLength;
        }

        if (current.Count > 0)
        {
            chunks.Add(new BridgeCommand.MergeLotState(Guid.NewGuid(), bundle with { Sims = current }));
        }

        return chunks;
    }

    private static string EncodeRawCommand(BridgeCommand.Raw raw)
    {
        string[] parts = new string[2 + (raw.Args?.Length ?? 0)];
        parts[0] = raw.Name;
        parts[1] = Id(raw.CorrelationId);
        for (int i = 0; i < (raw.Args?.Length ?? 0); i++)
        {
            parts[2 + i] = raw.Args![i];
        }

        return Join(parts);
    }

    /// <summary>
    /// wire-command inventory with usage hints — bridgectl's `commands` verb, and the
    /// P5.0 "search before templating" lesson made mechanical. Keep in sync with
    /// <see cref="EncodeCommand"/> and the mod's ProcessCommandLine dispatch.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownCommands = new[]
    {
        "CONNECT <32-zero-hex>  (handshake; sent automatically)",
        "LOAD_SAVE <hexSaveName>",
        "LOAD_SAVE_MENU <hexSaveName>",
        "SAVE_GAME <hexSaveName>",
        "SET_GAME_SPEED <Pause|Normal|Double|Triple|Skip>",
        "SET_WORLD_TIME <day:0-6> <hours:0-23> <minutes:0-59>  (DEV-116 absolute clock; SimClockUtils.SetStartTime)",
        "REQUEST_SNAPSHOT [sinceTick]  (heavy: chunked, use @60)",
        "BULK_REF snapshot|snapshot-part|diag|probe-upload (v11 bulk metadata)",
        "QUEUE_INTERACTION <actorId> <hexKey> <targetId|0> <hasPos:0|1> <x> <y> <z>",
        "SET_ACTIVE_SIM <simId>",
        "CANCEL_INTERACTION <actorId> <interactionId>",
        "LEAVE_CONVERSATION <actorId>  (server role; the Sim leaves its conversation via the engine's own cancel)",
        "APPLY_INTERACTION_QUEUE <actorId> <hexKey> <targetId|0> <hasPos:0|1> <x> <y> <z> [hexParameters]  (client role)",
        "APPLY_INTERACTIONS <actor:hexKey:targetId:hasPos:x:y:z[:hexParameters]>;...  (client role; DEV-134 batched replays)",
        "APPLY_FORCE_ALIGN <actorId> <hexKey> <postureContainerId> <isTransition:0|1> [hexParameters]  (client role; DEV-136 seat/posture alignment)",
        "INJECT_CLICK <simId> <x> <y> <z>  (client role; synthesizes a UserDirected GoHere through the capture interceptor)",
        "INJECT_READ_INTENT <simId> <bookObjectId> <hexMode>  (client role; UserDirected chooser|getbook — DEV-143)",
        "DISABLE_AUTONOMY <simId> [cancelRunning:0|1]",
        "ENABLE_AUTONOMY <simId> [reset]",
        "GATE_AUTONOMY <simId>  (DEV-125: register claimed Sim for command-lifecycle autonomy gate)",
        "UNGATE_AUTONOMY <simId> [reset]  (DEV-125: clear the command-lifecycle gate on release/leave)",
        "SET_SIM_SCOPE <lotId,...|-> <simId,...|->  (server role; W1: player lots/Sims for the forked SimScopeHook)",
        "SET_AUTHORITY <off|observe|enforce> <ownedSimId,...|->  (client role; W2: forked client authority gate)",
        "PROBE_SIM_STATE <simId,...>  (W2 part B: motives + buffs of up to 8 Sims)",
        "APPLY_SIM_STATE <simId:m0,..,m5:guid/origin+...|->  (client role; W2 part B: set mirrored motives + buffs)",
        "SET_SIM_ALIASES <serverObjectId:simDescriptionId,...|-> [reset]  (client role; W2 identity: resolve server Sim ids by description)",
        "PROBE_SIM_PRESENCE [sinceSeq]  (W2 identity: journal of Sims entering/leaving the world; first call takes the baseline)",
        "SPAWN_SIM <serverObjectId> <simDescriptionId> <x> <y> <z>  (client role; W2 identity: instantiate the server's Sim)",
        "DESPAWN_SIM <serverObjectId> <simDescriptionId>  (client role; W2 identity: remove a Sim the server removed)",
        "PROBE_PRESENTATION [lotId,...|-] [simId,...|-]",
        "SACS_RECORDER on|off|status|ring-on|ring-off|dump [sinceSeq [simId,...]]|snapshot <simId,...>|snapshot-driver <handle> [offset]",
        "SACS_REPLAY <hexLines>  (client role; replay the server's recorded animation calls)",
        "APPLY_AUTONOMY <simId> <disabled:0|1>  (client role)",
        "SET_ROLE <server|client>",
        "APPLY_POSITION <simId> <x> <y> <z>  (client role)",
        "APPLY_POSITIONS <simId:x:y:z>;<simId:x:y:z>;...  (client role; DEV-124 batched, one frame N Sims)",
        "MERGE_LOT_STATE <lotId> <simId:x:y:z>;<simId:x:y:z>;...  (server role; DEV-149 slice 6 lot-boundary reconcile before SAVE_GAME)",
        "APPLY_MOTIVE <simId> <hexMotive> <value>  (client role)",
        "APPLY_CASH <householdId> <funds>  (client role)",
        "APPLY_SKILL <simId> <hexSkill> <level>  (client role)",
        "PROBE_SIM_STATUS <simId>",
        "PROBE_SIM_INTERACTION_QUEUE <simId>",
        "PROBE_SIM_MOTIVE <simId> <hexMotive>",
        "PROBE_SIM_SKILL <simId> <hexSkill>",
        "PROBE_SIM_AUTONOMY <simId>",
        "PROBE_HOUSEHOLD_CASH <householdId>",
        "PROBE_SIM_LIFECYCLE",
        "PROBE_ACTOR_LOOKUP",
        "PROBE_STATE_INJECTION <simId> <x> <y> <z>",
        "NOTIFY <hexText>  (in-game toast)",
        "NOTICES [sinceSeq]",
        "DIAG_STATE",
        "DIAG_CAPTURE_INTERACTION <simId>  (capture current interaction replay bundle fields)",
        "DIAG_INTERACTION_STATE <simId> [hexBookId|-]  (posture/container/active interaction + ReadBook progress watchdog)",
        "DIAG_QUEUE <simId>  (full interaction queue + exit reasons)",
        "DIAG_RESOLVE <objectId>  (which resolution paths succeed)",
        "DIAG_WALK <objectId> <hexFieldPath|-> <depth:1-6> [maxNodes]  (reflection field dump; '-' = root)",
        "DIAG_BLOB <blobId> <index> <count> <hexChunk>  (DEV-108: inbound probe-DLL chunk upload)",
        "DIAG_LOADTEST  (DEV-108: Assembly.Load the embedded test asm; phase-1 mono gate)",
        "DIAG_RUN <hexTypeName>  (DEV-108: load last uploaded blob + invoke Type.Run())",
        "DIAG_RERUN  (DEV-108: re-invoke the last-loaded probe; no re-Assembly.Load)",
        "DIAG_HANDLERTEST  (DEV-120 T5b spike: load uploaded handler asm + invoke RunCtx via Converter carrier)",
        "RELOAD_HANDLERS [hexEntryType]  (DEV-122 T5d: hot-swap the last DIAG_BLOB-uploaded handler asm; no TS3 restart)",
        "PING",
        "DISCONNECT  (graceful controller shutdown; mod resets dedupe cursor + re-arms beacon; sent automatically)",
    };

    private static string Join(params string[] parts) => string.Join("|", parts);

    private static string Id(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string EncodeRole(HostRole role) => role switch
    {
        HostRole.Server => "server",
        HostRole.Client => "client",
        _ => throw new ArgumentException($"Unknown HostRole: {role}")
    };

    private static string EncodeSpeed(GameSpeed speed)
    {
        return speed switch
        {
            GameSpeed.Pause => "Pause",
            GameSpeed.Normal => "Normal",
            GameSpeed.Speed2 => "Double",
            GameSpeed.Speed3 => "Triple",
            GameSpeed.Ultra => "Skip",
            _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, "Unsupported game speed.")
        };
    }

    private static GameSpeed DecodeSpeed(string speed)
    {
        return speed switch
        {
            "Pause" => GameSpeed.Pause,
            "Normal" => GameSpeed.Normal,
            "Double" or "Speed2" => GameSpeed.Speed2,
            "Triple" or "Speed3" => GameSpeed.Speed3,
            "Skip" or "Ultra" => GameSpeed.Ultra,
            _ => throw new FormatException($"Unsupported game speed '{speed}'.")
        };
    }

    public static string Text(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        char[] chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i * 2] = HexChar((b >> 4) & 0x0F);
            chars[(i * 2) + 1] = HexChar(b & 0x0F);
        }

        return new string(chars);
    }

    public static string DecodeText(string value)
    {
        if ((value.Length & 1) != 0)
        {
            throw new FormatException("Encoded bridge text has an odd number of hex characters.");
        }

        byte[] bytes = new byte[value.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((HexValue(value[i * 2]) << 4) | HexValue(value[(i * 2) + 1]));
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static char HexChar(int value) =>
        (char)(value < 10 ? '0' + value : 'A' + (value - 10));

    private static int HexValue(char value)
    {
        if (value is >= '0' and <= '9') return value - '0';
        if (value is >= 'A' and <= 'F') return value - 'A' + 10;
        if (value is >= 'a' and <= 'f') return value - 'a' + 10;
        throw new FormatException("Invalid hex character in bridge text.");
    }
}
