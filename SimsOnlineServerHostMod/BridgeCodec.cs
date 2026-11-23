using System;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    internal sealed class BridgeCommand
    {
        public string Name;
        public string CorrelationId;
        public string[] Parts;
    }

    internal static class BridgeCodec
    {
        public static BridgeCommand DecodeCommand(string line)
        {
            string[] parts = line.Split('|');
            if (parts.Length < 2)
            {
                throw new InvalidOperationException("Bridge command is missing a correlation id.");
            }

            BridgeCommand command = new BridgeCommand();
            command.Name = parts[0];
            command.CorrelationId = parts[1];
            command.Parts = parts;
            return command;
        }

        public static string DecodeText(string value)
        {
            if (value == null || value.Length == 0)
            {
                return string.Empty;
            }

            if ((value.Length & 1) != 0)
            {
                throw new InvalidOperationException("Encoded text has an odd number of hex characters.");
            }

            byte[] bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((HexValue(value[i * 2]) << 4) | HexValue(value[(i * 2) + 1]));
            }

            return Encoding.UTF8.GetString(bytes);
        }

        // hex -> raw bytes (DecodeText assumes UTF-8 text; a probe DLL is binary).
        public static byte[] DecodeHexBytes(string value)
        {
            if (value == null || value.Length == 0)
            {
                return new byte[0];
            }

            if ((value.Length & 1) != 0)
            {
                throw new InvalidOperationException("Hex blob has an odd number of characters.");
            }

            byte[] bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((HexValue(value[i * 2]) << 4) | HexValue(value[(i * 2) + 1]));
            }

            return bytes;
        }

        public static string EncodeText(string value)
        {
            if (value == null)
            {
                value = string.Empty;
            }

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

        private static char HexChar(int value)
        {
            return (char)(value < 10 ? '0' + value : 'A' + (value - 10));
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'A' && value <= 'F')
            {
                return value - 'A' + 10;
            }

            if (value >= 'a' && value <= 'f')
            {
                return value - 'a' + 10;
            }

            throw new InvalidOperationException("Invalid hex character in encoded text.");
        }

        public static string Hello(string worldName, long tick)
        {
            // Wire version 5: optional build stamp lets the controller verify the
            // running mod matches the workspace (git sha + build UTC).
            return "HELLO|5|" + EncodeText(worldName) + "|" + tick.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(BuildStamp.WireForm());
        }

        public static string HelloV6(string worldName, long tick, string caps)
        {
            return "HELLO|6|" + EncodeText(worldName) + "|" + tick.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(BuildStamp.WireForm()) + "|" + EncodeText(caps);
        }

        public static string Ok(string correlationId)
        {
            return "OK|" + correlationId;
        }

        public static string Error(string correlationId, string reason)
        {
            return "ERROR|" + correlationId + "|" + EncodeText(reason);
        }

        public static string SessionReady(string correlationId, string saveName, string worldName, long tick)
        {
            return "SESSION_READY|" + correlationId + "|" + EncodeText(saveName) + "|" + EncodeText(worldName) + "|" + tick.ToString(CultureInfo.InvariantCulture);
        }

        public static string Snapshot(string correlationId, long tick, string worldName, string saveName, string speed)
        {
            return "SNAPSHOT|" + correlationId + "|" + tick.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(worldName) + "|" + EncodeText(saveName) + "|" + speed;
        }

        public static string Snapshot(string correlationId, long tick, string worldName, string saveName, string speed, string[] sims, string[] objects)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(Snapshot(correlationId, tick, worldName, saveName, speed));
            builder.Append("|");
            builder.Append(sims.Length.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < sims.Length; i++)
            {
                builder.Append("|");
                builder.Append(sims[i]);
            }

            builder.Append("|");
            builder.Append(objects.Length.ToString(CultureInfo.InvariantCulture));
            for (int j = 0; j < objects.Length; j++)
            {
                builder.Append("|");
                builder.Append(objects[j]);
            }

            return builder.ToString();
        }

        public static string SnapshotChunk(string correlationId, int index, int count, string payload)
        {
            return "SNAPSHOT_CHUNK|" + correlationId + "|" + index.ToString(CultureInfo.InvariantCulture) + "|" + count.ToString(CultureInfo.InvariantCulture) + "|" + payload;
        }

        public static string BulkRefSnapshot(string correlationId, int slotIndex, int totalLength, uint crc32)
        {
            return "BULK_REF|" + correlationId + "|snapshot|" + slotIndex.ToString(CultureInfo.InvariantCulture) + "|" + totalLength.ToString(CultureInfo.InvariantCulture) + "|" + crc32.ToString("X8", CultureInfo.InvariantCulture);
        }

        public static string BulkRefSnapshotPart(string correlationId, int slotIndex, int partIndex, int partCount, int totalLength, uint crc32)
        {
            return "BULK_REF|" + correlationId + "|snapshot-part|" + slotIndex.ToString(CultureInfo.InvariantCulture) + "|" + partIndex.ToString(CultureInfo.InvariantCulture) + "|" + partCount.ToString(CultureInfo.InvariantCulture) + "|" + totalLength.ToString(CultureInfo.InvariantCulture) + "|" + crc32.ToString("X8", CultureInfo.InvariantCulture);
        }

        // M4 P4.2.3: 3-arg overload becomes a delegator emitting null target
        // (used by PollAutonomyWatchQueues - autonomy watcher does not know target). The
        // canonical 8-arg form below carries target object id + position + hasPos flag
        // mirroring the QUEUE_INTERACTION / APPLY_INTERACTION_QUEUE wire shape.
        public static string EventInteractionQueued(ulong actorObjectId, ulong interactionId, string interactionKey)
        {
            return EventInteractionQueued(actorObjectId, interactionId, interactionKey, "", "0", "0", "0", "0");
        }

        public static string EventInteractionQueued(ulong actorObjectId, ulong interactionId, string interactionKey, string targetIdStr, string hasPositionStr, string xStr, string yStr, string zStr)
        {
            return "EVENT|InteractionQueued|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + interactionId.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(interactionKey) + "|" + targetIdStr + "|" + hasPositionStr + "|" + xStr + "|" + yStr + "|" + zStr;
        }

        // production bundle emit for claimed-Sim autonomous replay (replaces thin
        // InteractionQueued on the host emit path -- the bundle carries posture, parameters, policy).
        public static string EventInteractionReplayQueued(string captureReport)
        {
            return "EVENT|InteractionReplayQueued|" + EncodeText(captureReport);
        }

        public static string EventIntentCaptured(ulong actorObjectId, ulong targetObjectId, string interactionKey, string hasPositionStr, string xStr, string yStr, string zStr)
        {
            return "EVENT|IntentCaptured|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + targetObjectId.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(interactionKey) + "|" + hasPositionStr + "|" + xStr + "|" + yStr + "|" + zStr;
        }

        // input forwarding: a player's pick the client authority gate blocked (client role).
        // EVENT|UserIntent|actor|target|hexDefinitionType|hexName|hexPath|hit|x|y|z|picked
        public static string EventUserIntent(ulong actorObjectId, ulong targetObjectId, string definitionType, string name, string path, string hit, string xStr, string yStr, string zStr, string picked)
        {
            return "EVENT|UserIntent|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + targetObjectId.ToString(CultureInfo.InvariantCulture)
                + "|" + EncodeText(definitionType) + "|" + EncodeText(name) + "|" + EncodeText(path) + "|" + hit + "|" + xStr + "|" + yStr + "|" + zStr + "|" + picked;
        }

        // card 55: a walk asks the controller for a Rust plan. Built by RustRoutePlan.BridgeLine (engine-free, tested);
        // the controller's RoutePlanRequestLine parses it. EVENT|RoutePlanRequest|sim|sx|sy|sz|gx|gy|gz|corr
        // Card 61 uses the same pending-correlation wait, with a different answer channel.
        public static string EventRoadPlaceRequest(ulong corr, ulong obj, float x, float y, float z, int option,
            float distance, int radiusCm, string view)
        {
            return RustRoadPlace.BridgeLine(corr, obj, x, y, z, option, distance, radiusCm, view);
        }

        // queue mirror: the player cancelled a mirrored queue item in the HUD (client role).
        // EVENT|CancelIntent|serverSimObjectId|interactionId
        public static string EventCancelIntent(ulong simObjectId, ulong interactionId)
        {
            return "EVENT|CancelIntent|" + simObjectId.ToString(CultureInfo.InvariantCulture) + "|" + interactionId.ToString(CultureInfo.InvariantCulture);
        }

        // n14: the player left the active Sim's mirrored conversation from the HUD (client role).
        // EVENT|LeaveConversationIntent|serverSimObjectId
        public static string EventLeaveConversationIntent(ulong simObjectId)
        {
            return "EVENT|LeaveConversationIntent|" + simObjectId.ToString(CultureInfo.InvariantCulture);
        }

        // a claimed Sim's dialog for its player (server role). entry = { id, sim, kind, args... }.
        // EVENT|DialogRequest|id|sim|hexKind|argCount|hexArg...
        public static string EventDialogRequest(string[] entry)
        {
            StringBuilder sb = new StringBuilder("EVENT|DialogRequest|");
            sb.Append(entry[0]).Append('|').Append(entry[1]).Append('|').Append(EncodeText(entry[2])).Append('|');
            sb.Append((entry.Length - 3).ToString(CultureInfo.InvariantCulture));
            for (int i = 3; i < entry.Length; i++)
            {
                sb.Append('|').Append(EncodeText(entry[i]));
            }

            return sb.ToString();
        }

        // the player's answer to a remote dialog (client role). EVENT|DialogAnswer|id|sim|hexAnswer
        // ("-" = no answer: the server keeps the dialog's default).
        public static string EventDialogAnswer(int id, ulong sim, string answer)
        {
            return "EVENT|DialogAnswer|" + id.ToString(CultureInfo.InvariantCulture) + "|" + sim.ToString(CultureInfo.InvariantCulture)
                + "|" + (answer == null ? "-" : EncodeText(answer));
        }

        // P5 speed-input capture: client-role UI speed change reverted locally and forwarded
        // to the authoritative server (wire speed name matches SET_GAME_SPEED encoding).
        public static string EventSpeedIntentCaptured(string wireSpeed)
        {
            return "EVENT|SpeedIntentCaptured|" + wireSpeed;
        }

        // benign diagnostic the client interceptor emits when its forward allowlist
        // (ClientCaptureFilter) drops a UserDirected interaction the player did NOT click -- an
        // engine-pushed ReactionInteraction (or other engine UserDirected push) that would
        // otherwise be forwarded as a fake player click and rejected by the server's GoHere-only
        // allowlist ("Unsupported interaction key"). It lands in the raw bridge transcript
        // (client-bridge.log) so the drop is visible; the host's TryDecodeEvent does not recognize
        // this key (returns null) so it is silently ignored -- intentionally NOT an ERROR/rejection
        // line (must not match logq.WEDGE_PATTERNS, or the diagnostic would itself trip UT-1).
        public static string EventCaptureFilterDropped(ulong actorObjectId, string interactionKey)
        {
            return "EVENT|CaptureFilterDropped|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(interactionKey);
        }

        public static string EventInteractionStarted(ulong actorObjectId, ulong interactionId)
        {
            return "EVENT|InteractionStarted|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + interactionId.ToString(CultureInfo.InvariantCulture);
        }

        public static string EventInteractionCompleted(ulong actorObjectId, ulong interactionId)
        {
            return "EVENT|InteractionCompleted|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + interactionId.ToString(CultureInfo.InvariantCulture);
        }

        public static string EventInteractionFailed(ulong actorObjectId, ulong interactionId, string reason)
        {
            return "EVENT|InteractionFailed|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + interactionId.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(reason);
        }

        // a player's pick the server's queue accepted and then dropped. correlationId is the
        // QUEUE_INTERACTION command's (the player's command id); reason is ObjectInUse, RouteFailed,
        // FailedToStart or NotRun, and for the last two the server game's greyed-out tooltip can follow it after
        // UserIntentWire.TooltipMarker (card 57 row 57-b). The controller reads the reason as free text.
        public static string EventPickFailed(string correlationId, ulong actorObjectId, ulong interactionId, string reason)
        {
            return "EVENT|PickFailed|" + correlationId + "|" + actorObjectId.ToString(CultureInfo.InvariantCulture) + "|" + interactionId.ToString(CultureInfo.InvariantCulture) + "|" + EncodeText(reason);
        }

        public static string EventServerError(string reason)
        {
            return "EVENT|ServerError|" + EncodeText(reason);
        }

        public static string AutonomyProbe(string correlationId, string kind, string report)
        {
            return "AUTONOMY_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string SimLifecycleProbe(string correlationId, string kind, string report)
        {
            return "SIM_LIFECYCLE_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string StateInjectionProbe(string correlationId, string kind, string report)
        {
            return "STATE_INJECTION_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string ActorLookupProbe(string correlationId, string kind, string report)
        {
            return "ACTOR_LOOKUP_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string SimMotiveProbe(string correlationId, string kind, string report)
        {
            return "SIM_MOTIVE_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string SimInteractionQueueProbe(string correlationId, string kind, string report)
        {
            return "SIM_INTERACTION_QUEUE_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string HouseholdCashProbe(string correlationId, string kind, string report)
        {
            return "HOUSEHOLD_CASH_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string SimAutonomyProbe(string correlationId, string kind, string report)
        {
            return "SIM_AUTONOMY_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string SimStatusProbe(string correlationId, string kind, string report)
        {
            return "SIM_STATUS_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string SimSkillProbe(string correlationId, string kind, string report)
        {
            return "SIM_SKILL_PROBE|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        // re-attach diagnostic report (role, in-world, tick, autonomy-watched sims).
        public static string DiagStateReport(string correlationId, string kind, string report)
        {
            return "DIAG_STATE_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        // live game-state inspector reports.
        public static string DiagQueueReport(string correlationId, string kind, string report)
        {
            return "DIAG_QUEUE_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string DiagInteractionBundleReport(string correlationId, string kind, string report)
        {
            return "DIAG_INTERACTION_BUNDLE_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string DiagInteractionStateReport(string correlationId, string kind, string report)
        {
            return "DIAG_INTERACTION_STATE_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string DiagResolveReport(string correlationId, string kind, string report)
        {
            return "DIAG_RESOLVE_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string DiagWalkReport(string correlationId, string kind, string report)
        {
            return "DIAG_WALK_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        // (T5): hot-injection spike reports — same NAME|corrId|kind|hexReport shape.
        public static string DiagBlobReport(string correlationId, string kind, string report)
        {
            return "DIAG_BLOB_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string ExportHouseholdContentReport(
            string correlationId,
            ulong householdId,
            int slotIndex,
            int totalBytes,
            int propertyStreamBytes,
            int resKeyTableBytes,
            int objectIdTableBytes,
            uint crc32,
            int entryCount,
            int resKeyCount,
            int objectIdCount)
        {
            string report = "exported=true"
                + ";householdId=" + householdId.ToString(CultureInfo.InvariantCulture)
                + ";slotIndex=" + slotIndex.ToString(CultureInfo.InvariantCulture)
                + ";totalBytes=" + totalBytes.ToString(CultureInfo.InvariantCulture)
                + ";propertyStreamBytes=" + propertyStreamBytes.ToString(CultureInfo.InvariantCulture)
                + ";resKeyTableBytes=" + resKeyTableBytes.ToString(CultureInfo.InvariantCulture)
                + ";objectIdTableBytes=" + objectIdTableBytes.ToString(CultureInfo.InvariantCulture)
                + ";crc32=" + crc32.ToString("X8", CultureInfo.InvariantCulture)
                + ";entryCount=" + entryCount.ToString(CultureInfo.InvariantCulture)
                + ";resKeyCount=" + resKeyCount.ToString(CultureInfo.InvariantCulture)
                + ";objectIdCount=" + objectIdCount.ToString(CultureInfo.InvariantCulture)
                + ";inventoryCount=0";
            return DiagBlobReport(correlationId, "ok", report);
        }

        // extended report with inventory segments.
        public static string ExportHouseholdContentReportV2(
            string correlationId,
            ulong householdId,
            int slotIndex,
            int totalBytes,
            int propertyStreamBytes,
            int resKeyTableBytes,
            int objectIdTableBytes,
            uint crc32,
            int entryCount,
            int resKeyCount,
            int objectIdCount,
            int inventoryCount,
            string inventorySegmentSizes)
        {
            string report = "exported=true"
                + ";householdId=" + householdId.ToString(CultureInfo.InvariantCulture)
                + ";slotIndex=" + slotIndex.ToString(CultureInfo.InvariantCulture)
                + ";totalBytes=" + totalBytes.ToString(CultureInfo.InvariantCulture)
                + ";propertyStreamBytes=" + propertyStreamBytes.ToString(CultureInfo.InvariantCulture)
                + ";resKeyTableBytes=" + resKeyTableBytes.ToString(CultureInfo.InvariantCulture)
                + ";objectIdTableBytes=" + objectIdTableBytes.ToString(CultureInfo.InvariantCulture)
                + ";crc32=" + crc32.ToString("X8", CultureInfo.InvariantCulture)
                + ";entryCount=" + entryCount.ToString(CultureInfo.InvariantCulture)
                + ";resKeyCount=" + resKeyCount.ToString(CultureInfo.InvariantCulture)
                + ";objectIdCount=" + objectIdCount.ToString(CultureInfo.InvariantCulture)
                + ";inventoryCount=" + inventoryCount.ToString(CultureInfo.InvariantCulture)
                + ";inventorySegments=" + inventorySegmentSizes;
            return DiagBlobReport(correlationId, "ok", report);
        }

        public static string DiagLoadTestReport(string correlationId, string kind, string report)
        {
            return "DIAG_LOADTEST_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }

        public static string DiagRunReport(string correlationId, string kind, string report)
        {
            return "DIAG_RUN_REPORT|" + correlationId + "|" + kind + "|" + EncodeText(report);
        }
    }
}
