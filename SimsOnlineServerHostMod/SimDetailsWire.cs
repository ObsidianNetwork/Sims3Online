// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    // (docs/engine/sim-identity-and-npc-creation.md, step 5): the slow-changing details of a claimed
    // Sim that the client's UI and pie menus read from its own copy: the job (career guid, level, branch), its
    // household's funds and its skill levels. PROBE_SIM_DETAILS|corrId|<ids> (server) replies AUTONOMY_PROBE
    // ok with entries joined by ';' ("-" when none); APPLY_SIM_DETAILS|corrId|<entry> (client) applies one.
    // Entry: "<objectId>:<careerGuid>:<level>:<branch>:<funds>:<skills>"
    //   careerGuid 0 = no job (level 0, branch "-"); funds -1 = no household;
    //   skills "-" or "<SkillNames>=<level>+..." sorted by name, levels above 0 only.
    // Shared by the mod handlers and the controller (SimDetailsSample).
    public static class SimDetailsWire
    {
        public const int MaxSimsPerProbe = 8;

        // Branch names come from career tuning; keep them clear of the entry's separators.
        public static string CleanToken(string value)
        {
            if (value == null || value.Length == 0)
            {
                return "-";
            }

            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char ch = chars[i];
                if (ch == ':' || ch == ';' || ch == ',' || ch == '|' || ch == '+' || ch == '=' || ch == ' ')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        public static string FormatSkills(System.Collections.Generic.IList<string> names, System.Collections.Generic.IList<int> levels)
        {
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                if (levels[i] > 0)
                {
                    parts.Add(CleanToken(names[i]) + "=" + levels[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (parts.Count == 0)
            {
                return "-";
            }

            parts.Sort(System.StringComparer.Ordinal);
            return string.Join("+", parts.ToArray());
        }

        public static string FormatEntry(ulong objectId, ulong careerGuid, int level, string branch, int funds, string skills)
        {
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            return objectId.ToString(c) + ":" + careerGuid.ToString(c) + ":" + level.ToString(c) + ":"
                + CleanToken(careerGuid == 0uL ? null : branch) + ":" + funds.ToString(c) + ":" + (skills == null || skills.Length == 0 ? "-" : skills);
        }

        public static bool TryParseEntry(string entry, out ulong objectId, out ulong careerGuid, out int level, out string branch,
            out int funds, out string skills)
        {
            objectId = 0uL;
            careerGuid = 0uL;
            level = 0;
            branch = "-";
            funds = -1;
            skills = "-";
            if (entry == null)
            {
                return false;
            }

            string[] f = entry.Split(':');
            System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
            if (f.Length != 6
                || !ulong.TryParse(f[0], System.Globalization.NumberStyles.None, c, out objectId)
                || !ulong.TryParse(f[1], System.Globalization.NumberStyles.None, c, out careerGuid)
                || !int.TryParse(f[2], System.Globalization.NumberStyles.Integer, c, out level)
                || !int.TryParse(f[4], System.Globalization.NumberStyles.Integer, c, out funds)
                || f[3].Length == 0 || f[5].Length == 0)
            {
                return false;
            }

            branch = f[3];
            skills = f[5];
            return true;
        }

        // "-" or "Name=level+..." into parallel lists; false when a part is malformed.
        public static bool TryParseSkills(string skills, System.Collections.Generic.List<string> names, System.Collections.Generic.List<int> levels)
        {
            if (skills == null || skills == "-")
            {
                return true;
            }

            string[] parts = skills.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                int level;
                if (eq <= 0 || !int.TryParse(parts[i].Substring(eq + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out level))
                {
                    return false;
                }

                names.Add(parts[i].Substring(0, eq));
                levels.Add(level);
            }

            return true;
        }
    }
}
