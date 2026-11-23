using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// card 57 ruling 4 (qoder/card57-protocol lane): the JSON wire of the public protocol is
/// byte-pinned. <see cref="WireMessages"/> builds one of every registered <see cref="ClientCommand"/>
/// and <see cref="ServerEvent"/> kind with deterministic values; every encoded line must equal the
/// captured pre-move byte output in <see cref="ExpectedWire"/> (generated on base main 22c39767, before
/// the types moved to the SimsOnline.Protocol library). The move of the types between assemblies may
/// not change a single wire byte; any discriminator, property name, order or formatting drift fails here.
/// </summary>
internal static partial class Program
{
    [Test("PublicProtocolCodec.WireJsonPinnedBytes")]
    private static void PublicProtocolCodecWireJsonPinnedBytes()
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (string line in ExpectedWire.Split('\n'))
        {
            string row = line.TrimEnd('\r');
            if (row.Length == 0)
            {
                continue;
            }
            int tab = row.IndexOf('\t');
            Assert(tab > 0, "Expected wire line is malformed: " + row);
            expected[row[..tab]] = row[(tab + 1)..];
        }
        Assert(expected.Count == 55, $"The pinned wire table should hold 18 commands and 37 events, got {expected.Count}.");

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((string label, string json) in WireMessages())
        {
            Assert(expected.TryGetValue(label, out string? expectedJson), $"No pinned bytes for message kind '{label}'.");
            Assert(json == expectedJson, $"Wire bytes changed for '{label}'.\n  expected: {expectedJson}\n  actual:   {json}");
            Assert(seen.Add(label), $"Duplicate message kind in the pin table: '{label}'.");
        }
        Assert(seen.Count == expected.Count, $"The pin table has {expected.Count} kinds but {seen.Count} are constructed; a kind was added or removed without regenerating the table.");
    }

    // One of each registered kind, deterministic values; construction mirrors the scratch generator
    // scratch/wire-pin/ used to capture ExpectedWire on the untouched tree.
    private static readonly Guid WireId = Guid.Parse("11111111-2222-3333-4444-555566667777");

    private static IEnumerable<(string Label, string Json)> WireMessages()
    {
        yield return Cmd("join_session", new ClientCommand.JoinSession(WireId, 42, "alice", "on")
        {
            SentryTrace = "trace-1",
            SentryBaggage = "baggage-1",
        });
        yield return Cmd("leave_session", new ClientCommand.LeaveSession(WireId, 42));
        yield return Cmd("claim_sim", new ClientCommand.ClaimSim(WireId, 42, 500uL));
        yield return Cmd("release_sim", new ClientCommand.ReleaseSim(WireId, 42, 500uL));
        yield return Cmd("set_active_sim", new ClientCommand.SetActiveSim(WireId, 42, 500uL));
        yield return Cmd("queue_interaction", new ClientCommand.QueueInteraction(WireId, 42, new InteractionRequest(
            700uL, "Sims3.Gameplay.Queues.Chat", 800uL, new Vector3Dto(1.5f, 2.5f, 3.5f),
            new Dictionary<string, string> { ["ui.name"] = "Chat", ["ui.picked"] = "objRef" })));
        yield return Cmd("cancel_interaction", new ClientCommand.CancelInteraction(WireId, 42, 700uL, 4495uL));
        yield return Cmd("leave_conversation", new ClientCommand.LeaveConversation(WireId, 42, 700uL));
        yield return Cmd("answer_dialog", new ClientCommand.AnswerDialog(WireId, 42, 7, 500uL, "yes"));
        yield return Cmd("request_sim_description", new ClientCommand.RequestSimDescription(WireId, 42, 900uL));
        yield return Cmd("request_world", new ClientCommand.RequestWorld(WireId, 42));
        yield return Cmd("set_game_speed", new ClientCommand.SetGameSpeed(WireId, 42, GameSpeed.Speed2));
        yield return Cmd("set_world_time", new ClientCommand.SetWorldTime(WireId, 42, new WorldTime(3, 16, 30)));
        yield return Cmd("request_snapshot", new ClientCommand.RequestSnapshot(WireId, 42, 12345L));
        yield return Cmd("set_client_role", new ClientCommand.SetClientRole(WireId, 42, PlayerRole.Client));
        yield return Cmd("save_game", new ClientCommand.SaveGame(WireId, 42, "Save A"));
        yield return Cmd("ack_snapshot", new ClientCommand.AckSnapshot(WireId, 42, 987654321L));
        yield return Cmd("submit_lot_export", new ClientCommand.SubmitLotExport(WireId, 42, new LotExportBundle(
            42, 300uL, 1234L,
            new List<LotSimExport> { new LotSimExport(500uL, new Vector3Dto(1.25f, 2f, 3f), "Chat") },
            new HouseholdExportPayload(600uL, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 6 },
                1, 1, 1,
                new List<InventoryExportPayload> { new InventoryExportPayload(0, new byte[] { 7 }, new byte[] { 8 }, new byte[] { 9 }, 1) },
                new InventoryExportPayload(-1, new byte[] { 10 }, new byte[] { 11 }, new byte[] { 12 }, 1),
                null)), 5L));

        yield return Evt("session_joined", new ServerEvent.SessionJoined(42, "alice", "notice", new List<string> { "presentation_stream" }));
        yield return Evt("command_accepted", new ServerEvent.CommandAccepted(WireId));
        yield return Evt("command_rejected", new ServerEvent.CommandRejected(WireId, "nope"));
        yield return Evt("sim_claimed", new ServerEvent.SimClaimed(42, 500uL, 300uL));
        yield return Evt("sim_released", new ServerEvent.SimReleased(42, 500uL));
        yield return Evt("interaction_queued", new ServerEvent.InteractionQueued(700uL, 4495uL, "Sims3.Gameplay.Queues.Chat",
            800uL, new Vector3Dto(1.5f, 2.5f, 3.5f), new Dictionary<string, string> { ["bookId"] = "moby" }));
        yield return Evt("interaction_replay_queued", new ServerEvent.InteractionReplayQueued(new InteractionReplayBundle(
            InteractionReplayBundle.CurrentVersion, 700uL, 4495uL, "key", "Instance.Type", "Definition.Type",
            800uL, "Target.Type", new Vector3Dto(1.5f, 2.5f, 3.5f), "High", true, false, false,
            InteractionQueueRole.Head, "chair", 900uL, 12345L,
            new Dictionary<string, string> { ["seatArea"] = "a" }, ReplayPolicy.ReplayExact)));
        yield return Evt("interaction_started", new ServerEvent.InteractionStarted(700uL, 4495uL));
        yield return Evt("interaction_completed", new ServerEvent.InteractionCompleted(700uL, 4495uL));
        yield return Evt("interaction_failed", new ServerEvent.InteractionFailed(700uL, 4495uL, "refused"));
        yield return Evt("snapshot_delta", new ServerEvent.SnapshotDelta(WireSnapshot(), true));
        yield return Evt("full_snapshot", new ServerEvent.FullSnapshot(WireSnapshot()));
        yield return Evt("game_speed_changed", new ServerEvent.GameSpeedChanged(GameSpeed.Ultra));
        yield return Evt("world_time_changed", new ServerEvent.WorldTimeChanged(new WorldTime(3, 16, 30)));
        yield return Evt("save_checkpointed", new ServerEvent.SaveCheckpointed("Save A", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
        yield return Evt("server_error", new ServerEvent.ServerError("boom"));
        yield return Evt("client_role_changed", new ServerEvent.ClientRoleChanged(42, PlayerRole.Server));
        yield return Evt("sim_autonomy_changed", new ServerEvent.SimAutonomyChanged(500uL, true));
        yield return Evt("intent_captured", new ServerEvent.IntentCaptured(700uL, 800uL, "key", new Vector3Dto(1.5f, 2.5f, 3.5f)));
        yield return Evt("lot_local_authority_mode_changed", new ServerEvent.LotLocalAuthorityModeChanged(42, LotLocalAuthorityMode.ProxMirror));
        yield return Evt("lot_export_requested", new ServerEvent.LotExportRequested(42, 300uL, "checkpoint_freshness"));
        yield return Evt("lot_reconcile_acked", new ServerEvent.LotReconcileAcknowledged(42, 300uL, 5L));
        yield return Evt("sim_skill_changed", new ServerEvent.SimSkillChanged(500uL, "cooking", 3));
        yield return Evt("household_funds_changed", new ServerEvent.HouseholdFundsChanged(100uL, 25000));
        yield return Evt("sim_motive_changed", new ServerEvent.SimMotiveChanged(500uL, "Energy", 75.5f));
        yield return Evt("sim_state_changed", new ServerEvent.SimStateChanged(500uL,
            new List<float> { 1f, 2.5f, 3f, 4f, 5f, 6.25f }, new List<SimBuffDto> { new SimBuffDto(11uL, 22uL) }));
        yield return Evt("sim_spawned", new ServerEvent.SimSpawned(500uL, 900uL, new Vector3Dto(1.5f, 2f, 3f)));
        yield return Evt("sim_despawned", new ServerEvent.SimDespawned(500uL, 900uL));
        yield return Evt("sim_description_data", new ServerEvent.SimDescriptionData(900uL, new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 6 }));
        yield return Evt("sim_details_changed", new ServerEvent.SimDetailsChanged(500uL, 123uL, 4, "branch", 25000, "Cooking=3"));
        yield return Evt("world_file_chunk", new ServerEvent.WorldFileChunk("Save A.sims3", 0, 2, "Save A.nhd", 0L, 4096L, new byte[] { 1, 2, 3 }));
        yield return Evt("sim_animation", new ServerEvent.SimAnimation(new List<string> { "1|A|abc", "2|B|def" }));
        yield return Evt("user_intent_captured", new ServerEvent.UserIntentCaptured(700uL, 800uL,
            "Sims3.Gameplay.Interactions.SuperInteraction", "Chat", "p1/p2", "hit point", new Vector3Dto(1.5f, 2.5f, 3.5f), "objRef"));
        yield return Evt("cancel_intent_captured", new ServerEvent.CancelIntentCaptured(700uL, 4495uL));
        yield return Evt("leave_conversation_intent_captured", new ServerEvent.LeaveConversationIntentCaptured(700uL));
        yield return Evt("dialog_requested", new ServerEvent.DialogRequested(7, 500uL, "TwoButton", new List<string> { "Yes", "No" }));
        yield return Evt("dialog_answer_captured", new ServerEvent.DialogAnswerCaptured(7, 500uL, "yes"));
    }

    private static SimulationSnapshot WireSnapshot() => new SimulationSnapshot(
        7L, "WorldA", "Save A", GameSpeed.Normal,
        new List<SimSnapshot> { new SimSnapshot(10uL, "Ada", 100uL, new Vector3Dto(1f, 2f, 3f), 4495uL, 42, "Chat", 300uL, 2, 4500uL) },
        new List<ObjectSnapshot> { new ObjectSnapshot(30uL, "Chair", new Vector3Dto(7.25f, 8f, 9.5f), 300uL) });

    private static (string, string) Cmd(string label, ClientCommand command) => (label, PublicProtocolCodec.EncodeCommand(command));

    private static (string, string) Evt(string label, ServerEvent serverEvent) => (label, PublicProtocolCodec.EncodeEvent(serverEvent));

    // Captured on the untouched tree (base main 22c39767) with scratch/wire-pin, before the protocol
    // types moved to SimsOnline.Protocol. One "label<TAB>json" line per kind; do not edit by hand —
    // regenerate only by proving the bytes unchanged.
    private const string ExpectedWire = @"
join_session	{""$type"":""join_session"",""displayName"":""alice"",""onScreen"":""on"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42,""sentryTrace"":""trace-1"",""sentryBaggage"":""baggage-1""}
leave_session	{""$type"":""leave_session"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
claim_sim	{""$type"":""claim_sim"",""simObjectId"":500,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
release_sim	{""$type"":""release_sim"",""simObjectId"":500,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
set_active_sim	{""$type"":""set_active_sim"",""simObjectId"":500,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
queue_interaction	{""$type"":""queue_interaction"",""interaction"":{""actorObjectId"":700,""interactionKey"":""Sims3.Gameplay.Queues.Chat"",""targetObjectId"":800,""targetPosition"":{""x"":1.5,""y"":2.5,""z"":3.5},""parameters"":{""ui.name"":""Chat"",""ui.picked"":""objRef""}},""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
cancel_interaction	{""$type"":""cancel_interaction"",""actorObjectId"":700,""interactionId"":4495,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
leave_conversation	{""$type"":""leave_conversation"",""actorObjectId"":700,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
answer_dialog	{""$type"":""answer_dialog"",""requestId"":7,""simObjectId"":500,""answer"":""yes"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
request_sim_description	{""$type"":""request_sim_description"",""simDescriptionId"":900,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
request_world	{""$type"":""request_world"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
set_game_speed	{""$type"":""set_game_speed"",""speed"":""speed2"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
set_world_time	{""$type"":""set_world_time"",""time"":{""day"":3,""hours"":16,""minutes"":30,""hours24"":16.5},""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
request_snapshot	{""$type"":""request_snapshot"",""sinceTick"":12345,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
set_client_role	{""$type"":""set_client_role"",""role"":""client"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
save_game	{""$type"":""save_game"",""saveName"":""Save A"",""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
ack_snapshot	{""$type"":""ack_snapshot"",""tick"":987654321,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
submit_lot_export	{""$type"":""submit_lot_export"",""bundle"":{""playerId"":42,""lotId"":300,""clientTick"":1234,""sims"":[{""objectId"":500,""position"":{""x"":1.25,""y"":2,""z"":3},""currentInteractionKey"":""Chat""}],""deepExport"":{""householdId"":600,""propertyStreamBytes"":""AQID"",""resKeyTableBytes"":""BAU="",""objectIdTableBytes"":""Bg=="",""entryCount"":1,""resKeyCount"":1,""objectIdCount"":1,""simInventories"":[{""slotIndex"":0,""propertyStreamBytes"":""Bw=="",""resKeyTableBytes"":""CA=="",""objectIdTableBytes"":""CQ=="",""itemCount"":1}],""sharedFridgeInventory"":{""slotIndex"":-1,""propertyStreamBytes"":""Cg=="",""resKeyTableBytes"":""Cw=="",""objectIdTableBytes"":""DA=="",""itemCount"":1}}},""clientLastReconciledVersion"":5,""commandId"":""11111111-2222-3333-4444-555566667777"",""playerId"":42}
session_joined	{""$type"":""session_joined"",""playerId"":42,""displayName"":""alice"",""notice"":""notice"",""capabilities"":[""presentation_stream""]}
command_accepted	{""$type"":""command_accepted"",""commandId"":""11111111-2222-3333-4444-555566667777""}
command_rejected	{""$type"":""command_rejected"",""commandId"":""11111111-2222-3333-4444-555566667777"",""reason"":""nope""}
sim_claimed	{""$type"":""sim_claimed"",""playerId"":42,""simObjectId"":500,""lotId"":300}
sim_released	{""$type"":""sim_released"",""playerId"":42,""simObjectId"":500}
interaction_queued	{""$type"":""interaction_queued"",""actorObjectId"":700,""interactionId"":4495,""interactionKey"":""Sims3.Gameplay.Queues.Chat"",""targetObjectId"":800,""targetPosition"":{""x"":1.5,""y"":2.5,""z"":3.5},""parameters"":{""bookId"":""moby""}}
interaction_replay_queued	{""$type"":""interaction_replay_queued"",""bundle"":{""version"":1,""actorObjectId"":700,""interactionId"":4495,""interactionKey"":""key"",""interactionInstanceType"":""Instance.Type"",""interactionDefinitionType"":""Definition.Type"",""targetObjectId"":800,""targetObjectType"":""Target.Type"",""targetPosition"":{""x"":1.5,""y"":2.5,""z"":3.5},""priorityLevel"":""High"",""isUserDirected"":true,""isAutonomous"":false,""isTransitionInteraction"":false,""queueRole"":""head"",""postureType"":""chair"",""postureContainerObjectId"":900,""hostTick"":12345,""parameters"":{""seatArea"":""a""},""policyHint"":""replayExact""}}
interaction_started	{""$type"":""interaction_started"",""actorObjectId"":700,""interactionId"":4495}
interaction_completed	{""$type"":""interaction_completed"",""actorObjectId"":700,""interactionId"":4495}
interaction_failed	{""$type"":""interaction_failed"",""actorObjectId"":700,""interactionId"":4495,""reason"":""refused""}
snapshot_delta	{""$type"":""snapshot_delta"",""snapshot"":{""tick"":7,""worldName"":""WorldA"",""saveName"":""Save A"",""speed"":""normal"",""sims"":[{""objectId"":10,""name"":""Ada"",""householdId"":100,""position"":{""x"":1,""y"":2,""z"":3},""currentInteractionId"":4495,""owningPlayerId"":42,""currentInteractionKey"":""Chat"",""lotId"":300,""interactionQueueCount"":2,""postureContainerObjectId"":4500}],""objects"":[{""objectId"":30,""scriptClass"":""Chair"",""position"":{""x"":7.25,""y"":8,""z"":9.5},""lotId"":300}]},""forceFull"":true}
full_snapshot	{""$type"":""full_snapshot"",""snapshot"":{""tick"":7,""worldName"":""WorldA"",""saveName"":""Save A"",""speed"":""normal"",""sims"":[{""objectId"":10,""name"":""Ada"",""householdId"":100,""position"":{""x"":1,""y"":2,""z"":3},""currentInteractionId"":4495,""owningPlayerId"":42,""currentInteractionKey"":""Chat"",""lotId"":300,""interactionQueueCount"":2,""postureContainerObjectId"":4500}],""objects"":[{""objectId"":30,""scriptClass"":""Chair"",""position"":{""x"":7.25,""y"":8,""z"":9.5},""lotId"":300}]}}
game_speed_changed	{""$type"":""game_speed_changed"",""speed"":""ultra""}
world_time_changed	{""$type"":""world_time_changed"",""time"":{""day"":3,""hours"":16,""minutes"":30,""hours24"":16.5}}
save_checkpointed	{""$type"":""save_checkpointed"",""saveName"":""Save A"",""at"":""2026-01-02T03:04:05+00:00""}
server_error	{""$type"":""server_error"",""message"":""boom""}
client_role_changed	{""$type"":""client_role_changed"",""playerId"":42,""role"":""server""}
sim_autonomy_changed	{""$type"":""sim_autonomy_changed"",""simObjectId"":500,""disabled"":true}
intent_captured	{""$type"":""intent_captured"",""actorObjectId"":700,""targetObjectId"":800,""interactionKey"":""key"",""targetPosition"":{""x"":1.5,""y"":2.5,""z"":3.5}}
lot_local_authority_mode_changed	{""$type"":""lot_local_authority_mode_changed"",""playerId"":42,""mode"":""proxMirror""}
lot_export_requested	{""$type"":""lot_export_requested"",""playerId"":42,""lotId"":300,""reason"":""checkpoint_freshness""}
lot_reconcile_acked	{""$type"":""lot_reconcile_acked"",""playerId"":42,""lotId"":300,""version"":5}
sim_skill_changed	{""$type"":""sim_skill_changed"",""simObjectId"":500,""skillName"":""cooking"",""level"":3}
household_funds_changed	{""$type"":""household_funds_changed"",""householdId"":100,""funds"":25000}
sim_motive_changed	{""$type"":""sim_motive_changed"",""simObjectId"":500,""motiveName"":""Energy"",""value"":75.5}
sim_state_changed	{""$type"":""sim_state_changed"",""simObjectId"":500,""motives"":[1,2.5,3,4,5,6.25],""buffs"":[{""guid"":11,""origin"":22}]}
sim_spawned	{""$type"":""sim_spawned"",""simObjectId"":500,""simDescriptionId"":900,""position"":{""x"":1.5,""y"":2,""z"":3}}
sim_despawned	{""$type"":""sim_despawned"",""simObjectId"":500,""simDescriptionId"":900}
sim_description_data	{""$type"":""sim_description_data"",""simDescriptionId"":900,""propertyStream"":""AQID"",""resKeyTable"":""BAU="",""objectIdTable"":""Bg==""}
sim_details_changed	{""$type"":""sim_details_changed"",""simObjectId"":500,""careerGuid"":123,""careerLevel"":4,""careerBranch"":""branch"",""funds"":25000,""skills"":""Cooking=3""}
world_file_chunk	{""$type"":""world_file_chunk"",""folderName"":""Save A.sims3"",""fileIndex"":0,""fileCount"":2,""fileName"":""Save A.nhd"",""offset"":0,""fileLength"":4096,""data"":""AQID""}
sim_animation	{""$type"":""sim_animation"",""lines"":[""1|A|abc"",""2|B|def""]}
user_intent_captured	{""$type"":""user_intent_captured"",""actorObjectId"":700,""targetObjectId"":800,""definitionType"":""Sims3.Gameplay.Interactions.SuperInteraction"",""name"":""Chat"",""path"":""p1/p2"",""hit"":""hit point"",""hitPoint"":{""x"":1.5,""y"":2.5,""z"":3.5},""picked"":""objRef""}
cancel_intent_captured	{""$type"":""cancel_intent_captured"",""actorObjectId"":700,""interactionId"":4495}
leave_conversation_intent_captured	{""$type"":""leave_conversation_intent_captured"",""actorObjectId"":700}
dialog_requested	{""$type"":""dialog_requested"",""requestId"":7,""simObjectId"":500,""kind"":""TwoButton"",""args"":[""Yes"",""No""]}
dialog_answer_captured	{""$type"":""dialog_answer_captured"",""requestId"":7,""simObjectId"":500,""answer"":""yes""}
";
}
