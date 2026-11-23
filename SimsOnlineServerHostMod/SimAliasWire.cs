// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    // W2 Sim identity (internal documentation): the pair-list field of
    // SET_SIM_ALIASES|corrId|pairs. Each pair is "<serverObjectId>:<simDescriptionId>", pairs joined
    // by ',', "-" for none. A description id of 0 removes the alias. Shared by the controller encoder
    // (BridgeLineCodec) and the mod handler (HandleSetSimAliases).
    public static class SimAliasWire
    {
        public const string Empty = "-";

        // Keeps one command line short; the controller splits larger batches.
        public const int MaxPairsPerCommand = 64;

        public static string Format(System.Collections.Generic.IList<ulong> objectIds, System.Collections.Generic.IList<ulong> descriptionIds)
        {
            if (objectIds == null || descriptionIds == null || objectIds.Count == 0)
            {
                return Empty;
            }

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int i = 0; i < objectIds.Count && i < descriptionIds.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(objectIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(descriptionIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        // False when any pair is malformed (the caller rejects the command).
        public static bool TryParse(string field, out ulong[] objectIds, out ulong[] descriptionIds)
        {
            objectIds = null;
            descriptionIds = null;
            if (field == null || field.Length == 0 || field == Empty)
            {
                objectIds = new ulong[0];
                descriptionIds = new ulong[0];
                return true;
            }

            string[] pairs = field.Split(',');
            ulong[] objects = new ulong[pairs.Length];
            ulong[] descriptions = new ulong[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                int colon = pairs[i].IndexOf(':');
                if (colon <= 0
                    || !ulong.TryParse(pairs[i].Substring(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out objects[i])
                    || !ulong.TryParse(pairs[i].Substring(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out descriptions[i]))
                {
                    return false;
                }
            }

            objectIds = objects;
            descriptionIds = descriptions;
            return true;
        }
    }
}
