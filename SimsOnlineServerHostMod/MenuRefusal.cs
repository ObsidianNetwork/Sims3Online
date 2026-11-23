// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    // part A step 3: a player's menu greys what the server refused. A client's pie menu runs the client's own
    // tests on its own copies, and some read state the mod does not mirror: a newspaper's old flag (Newspaper.IsReadable),
    // who is using an object (GameObject.InUse, PartData.CanBeUsedBy; the part A sweep, internal documentation).
    // So the player can pick an entry the server refuses. When the server refuses a player's pick on the definition's own
    // test (or has no such entry for that actor on that target), it sends
    //   Rf|actor|target|hexDefinitionType|hexName|1|hexTooltip
    // and tests the pick again at each prop poll; once the test passes, or the actor or the target is gone, it sends the
    // same line ending in 0 with an empty field 7. Field 7 is the server game's own greyed-out tooltip for that entry,
    // empty when its test gave none (card 57 row 57-a); a line without it is an older server's and still greys. A
    // client's presentation module holds the refused entries (Presentation/Refusals.cs), and the fork's menu loop
    // (PieMenu.TestAndBringUpPieMenu, the menu-refusal slice) shows such an entry greyed.
    // Public: the presentation module, a separate assembly, reads the lines with it.
    public static class MenuRefusal
    {
        public const string Kind = "Rf";

        // An older server's line: no field 7, so the client has no tooltip to show.
        public static string Line(ulong actor, ulong target, string definitionType, string name, bool refused)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return Kind + "|" + actor.ToString(c) + "|" + target.ToString(c) + "|" + Hex(definitionType) + "|" + Hex(name)
                + "|" + (refused ? "1" : "0");
        }

        // The same line with the tooltip the server game's own test gave for this entry, hex of UTF-8 on one line and
        // empty when there is none. A clear has nothing left to explain, so it sends field 7 empty.
        public static string Line(ulong actor, ulong target, string definitionType, string name, bool refused, string tooltip)
        {
            return Line(actor, target, definitionType, name, refused) + "|" + Hex(refused ? UserIntentWire.Flatten(tooltip) : null);
        }

        // A ring line as the replay splits it: f[0] the ring sequence, f[1] "Rf". False on anything malformed.
        // A line with only six fields is an older server's; it parses with no tooltip.
        public static bool Parse(string[] f, out ulong actor, out ulong target, out string definitionType, out string name, out bool refused)
        {
            actor = 0uL;
            target = 0uL;
            definitionType = null;
            name = null;
            refused = false;
            if (f == null || f.Length < 7 || f[1] != Kind || (f[6] != "0" && f[6] != "1"))
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            ulong a;
            ulong t;
            string type;
            string text;
            if (!ulong.TryParse(f[2], NumberStyles.None, c, out a) || !ulong.TryParse(f[3], NumberStyles.None, c, out t)
                || a == 0uL || t == 0uL || !Unhex(f[4], out type) || type.Length == 0 || !Unhex(f[5], out text))
            {
                return false;
            }

            actor = a;
            target = t;
            definitionType = type;
            name = text;
            refused = f[6] == "1";
            return true;
        }

        // As Parse, and field 7 read back: the tooltip the server game's own test gave for this entry. Null when the line
        // has no field 7 (an older server), when it is empty, or when it is not hex - a bad tooltip costs the reason, not
        // the grey.
        public static bool Parse(string[] f, out ulong actor, out ulong target, out string definitionType, out string name,
            out bool refused, out string tooltip)
        {
            tooltip = null;
            if (!Parse(f, out actor, out target, out definitionType, out name, out refused))
            {
                return false;
            }

            string held;
            if (f.Length > 7 && Unhex(f[7], out held) && held.Length > 0)
            {
                tooltip = held;
            }

            return true;
        }

        // The client's table key, in that game's own ids. The fork's hook (PresenterHook.ServerRefused) builds the same
        // string: decimal ids, invariant culture, the definition's full type name.
        public static string Key(ulong actor, ulong target, string definitionType)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return actor.ToString(c) + "|" + target.ToString(c) + "|" + definitionType;
        }

        public static string Hex(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value == null ? string.Empty : value);
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        public static bool Unhex(string value, out string text)
        {
            text = null;
            if (value == null || (value.Length & 1) != 0)
            {
                return false;
            }

            byte[] bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = Nibble(value[i * 2]);
                int lo = Nibble(value[i * 2 + 1]);
                if (hi < 0 || lo < 0)
                {
                    return false;
                }

                bytes[i] = (byte)((hi << 4) | lo);
            }

            text = Encoding.UTF8.GetString(bytes);
            return true;
        }

        private static int Nibble(char ch)
        {
            if (ch >= '0' && ch <= '9')
            {
                return ch - '0';
            }

            if (ch >= 'a' && ch <= 'f')
            {
                return ch - 'a' + 10;
            }

            if (ch >= 'A' && ch <= 'F')
            {
                return ch - 'A' + 10;
            }

            return -1;
        }
    }
}
