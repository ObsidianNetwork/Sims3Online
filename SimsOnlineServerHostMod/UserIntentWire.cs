// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    // input forwarding (docs/engine/input-forwarding.md): the parts of a player's pick that are
    // not plain ids, shared by the client mod (EVENT|UserIntent), the controller (QUEUE_INTERACTION's
    // user-pick fields) and the server mod (HandleQueueUserInteraction).
    //   hit     "<GameObjectHitType>:<objectId>:<layerMask>"
    //   picked  picker selections, "<kind><id>" joined by ',', "-" for none. Kinds: 's' a Sim (server
    //           object id), 'o' another object, 'd' a SimDescription (by SimDescriptionId).
    //   refusal a pick the server's queue refused on the definition's test: the ERROR reason (and so the player's
    //           command_rejected reason) ends with TooltipMarker and the server game's own greyed-out tooltip, when
    //           the test gave one (internal documentation). A reason without the marker has none. The same
    //           suffix rides an EVENT|PickFailed code that tells the player nothing (PickCodeTakesTooltip, card 57
    //           row 57-b).
    public static class UserIntentWire
    {
        public const string Empty = "-";

        public const string TooltipMarker = " | tooltip: ";

        // The text on one line (CR and LF to spaces) and trimmed, so it can ride one field of a bridge line; null in,
        // null out. A greyed-out tooltip comes from the game with line breaks in it.
        public static string Flatten(string text)
        {
            return text == null ? null : text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // The reason with the tooltip appended on one line; the reason unchanged when the tooltip is null or blank.
        public static string WithTooltip(string reason, string tooltip)
        {
            if (tooltip == null)
            {
                return reason;
            }

            string flat = Flatten(tooltip);
            return flat.Length == 0 ? reason : reason + TooltipMarker + flat;
        }

        // Which of the four EVENT|PickFailed codes takes the server game's greyed-out tooltip after it
        // (card 57 row 57-b). ObjectInUse and RouteFailed already name the cause to the player; NotRun and FailedToStart
        // read "right now" without one, so the server runs the definition's test again for those two and for those two
        // only.
        public static bool PickCodeTakesTooltip(string code)
        {
            return code == "NotRun" || code == "FailedToStart";
        }

        // The tooltip a refusal reason carries, or null when it carries none.
        public static string TooltipOf(string reason)
        {
            if (reason == null)
            {
                return null;
            }

            int at = reason.IndexOf(TooltipMarker, System.StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            string tooltip = reason.Substring(at + TooltipMarker.Length).Trim();
            return tooltip.Length == 0 ? null : tooltip;
        }

        public static string FormatHit(int hitType, ulong objectId, uint layerMask)
        {
            return hitType.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                + objectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                + layerMask.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool TryParseHit(string text, out int hitType, out ulong objectId, out uint layerMask)
        {
            hitType = 0;
            objectId = 0uL;
            layerMask = 0u;
            if (text == null)
            {
                return false;
            }

            string[] parts = text.Split(':');
            return parts.Length == 3
                && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out hitType)
                && ulong.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out objectId)
                && uint.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out layerMask);
        }

        public static string FormatPicked(System.Collections.Generic.IList<char> kinds, System.Collections.Generic.IList<ulong> ids)
        {
            if (kinds == null || ids == null || kinds.Count == 0)
            {
                return Empty;
            }

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int i = 0; i < kinds.Count && i < ids.Count; i++)
            {
                if (text.Length > 0)
                {
                    text.Append(',');
                }

                text.Append(kinds[i]).Append(ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.Length == 0 ? Empty : text.ToString();
        }

        // False for malformed text; "-" (or empty) parses to no selections.
        public static bool TryParsePicked(string text, out char[] kinds, out ulong[] ids)
        {
            kinds = new char[0];
            ids = new ulong[0];
            if (text == null || text.Length == 0 || text == Empty)
            {
                return true;
            }

            string[] parts = text.Split(',');
            char[] parsedKinds = new char[parts.Length];
            ulong[] parsedIds = new ulong[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length < 2 || (part[0] != 's' && part[0] != 'o' && part[0] != 'd'))
                {
                    return false;
                }

                if (!ulong.TryParse(part.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsedIds[i]))
                {
                    return false;
                }

                parsedKinds[i] = part[0];
            }

            kinds = parsedKinds;
            ids = parsedIds;
            return true;
        }
    }
}
