// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    // W1 server simulation scope (internal documentation): the id-list field shape of
    // SET_SIM_SCOPE|corrId|lots|sims. Decimal ulongs joined by ',', "-" for an empty list. Shared
    // by the controller encoder (BridgeLineCodec) and the mod handler (HandleSetSimScope).
    public static class SimScopeWire
    {
        public const string Empty = "-";

        public static string Format(System.Collections.Generic.IList<ulong> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return Empty;
            }

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }

                text.Append(ids[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        // (docs/engine/scope-hooks.md): true when any of a household's members is in the claimed set. The
        // fork's H1 makes such a household selectable on the server, so the GoHome gate (MethodSwap) asks the same
        // question of a Sim's household, not of the Sim alone. Null or empty lists answer false; no allocation.
        public static bool AnyClaimed(ulong[] claimed, ulong[] members)
        {
            if (claimed == null || members == null)
            {
                return false;
            }

            for (int i = 0; i < members.Length; i++)
            {
                for (int j = 0; j < claimed.Length; j++)
                {
                    if (members[i] != 0uL && members[i] == claimed[j])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Returns null when any element is not a decimal ulong (the caller rejects the command).
        public static ulong[] Parse(string field)
        {
            if (field == null || field.Length == 0 || field == Empty)
            {
                return new ulong[0];
            }

            string[] parts = field.Split(',');
            ulong[] ids = new ulong[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                ulong id;
                if (!ulong.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id))
                {
                    return null;
                }

                ids[i] = id;
            }

            return ids;
        }
    }
}
