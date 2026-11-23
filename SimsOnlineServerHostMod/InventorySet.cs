// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    // part A: an inventory on a client holds what the server's holds. A client's pie menu runs the client's own
    // tests on its own copies, and some tests read an inventory: Bookshelf_ReadSomething.Definition.Test is only
    // target.Inventory.Find<BookGeneral>() != null (Bookshelf_ReadSomething.cs:34-36), so a shelf whose only book the
    // server's Sim had taken out (Bookshelf.Read_GetBook: Inventory.TryToRemove, then the carry) still offered Read on
    // the other player's client. The server sends a watched inventory's whole set when it changes and now and then again:
    //   Iv|owner|item,item,...|Type,Type,...   (ids ascending, each once; "-" = empty; each item's type name, same order)
    // and the client moves its objects in or out of its copy of the owner's inventory (Presentation/Inventories.cs).
    // The types are for items the server made at runtime (Milton Tong's BookGeneral, 2026-10-21): no client has their
    // ids, and each client's copy of the owner holds its own object instead, which the client took out and left in
    // neither the inventory nor the scene. The client pairs such an item with its own object of the same type (Pair).
    // A line without types (an older server) pairs nothing. Public: the presentation module, a separate assembly,
    // reads the lines with it.
    public static class InventorySet
    {
        public const string Kind = "Iv";

        // A larger set (a full fridge) is not sent; that owner's menus keep the client's own state.
        public const int MaxItems = 200;

        public static string Membership(List<ulong> items)
        {
            if (items == null || items.Count == 0)
            {
                return "-";
            }

            List<ulong> sorted = new List<ulong>(items);
            sorted.Sort();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0 && sorted[i] == sorted[i - 1])
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(sorted[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        // The membership as above, and each kept item's type in the same order (typeList; "-" when empty). types runs
        // parallel to items; a type name holding ',' or '|' is written with '_' there.
        public static string Membership(List<ulong> items, List<string> types, out string typeList)
        {
            typeList = "-";
            if (items == null || items.Count == 0)
            {
                return "-";
            }

            List<int> order = new List<int>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                order.Add(i);
            }

            order.Sort(delegate(int a, int b) { return items[a] != items[b] ? items[a].CompareTo(items[b]) : a.CompareTo(b); });
            StringBuilder ids = new StringBuilder();
            StringBuilder names = new StringBuilder();
            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                if (k > 0 && items[i] == items[order[k - 1]])
                {
                    continue;
                }

                if (ids.Length > 0)
                {
                    ids.Append(',');
                    names.Append(',');
                }

                ids.Append(items[i].ToString(CultureInfo.InvariantCulture));
                string name = types != null && i < types.Count && !string.IsNullOrEmpty(types[i]) ? types[i] : "?";
                names.Append(name.Replace(',', '_').Replace('|', '_'));
            }

            typeList = names.ToString();
            return ids.ToString();
        }

        public static string Line(ulong owner, string membership)
        {
            return Kind + "|" + owner.ToString(CultureInfo.InvariantCulture) + "|" + membership;
        }

        public static string Line(ulong owner, string membership, string typeList)
        {
            return membership == "-" || string.IsNullOrEmpty(typeList) || typeList == "-"
                ? Line(owner, membership)
                : Line(owner, membership) + "|" + typeList;
        }

        // As Parse, with each item's type (none when the line has no types or their count is not the items').
        public static bool Parse(string[] f, out ulong owner, out List<ulong> items, out List<string> types)
        {
            types = new List<string>();
            if (!Parse(f, out owner, out items))
            {
                return false;
            }

            if (f.Length >= 5 && items.Count > 0)
            {
                string[] names = f[4].Split(',');
                if (names.Length == items.Count)
                {
                    types.AddRange(names);
                }
            }

            return true;
        }

        // Runtime items: the server's items this client has no object for (missing), each paired with this client's own
        // object of the same type that no server set named (own), in order, each used once. "?" pairs with nothing.
        public static void Pair(List<ulong> missing, List<string> missingTypes, List<ulong> own, List<string> ownTypes,
            List<ulong> pairedServer, List<ulong> pairedLocal)
        {
            if (missingTypes == null || missingTypes.Count != missing.Count || ownTypes == null || ownTypes.Count != own.Count)
            {
                return;
            }

            bool[] used = new bool[own.Count];
            for (int i = 0; i < missing.Count; i++)
            {
                if (missingTypes[i] == "?")
                {
                    continue;
                }

                for (int j = 0; j < own.Count; j++)
                {
                    if (!used[j] && ownTypes[j] == missingTypes[i])
                    {
                        used[j] = true;
                        pairedServer.Add(missing[i]);
                        pairedLocal.Add(own[j]);
                        break;
                    }
                }
            }
        }

        // A ring line as the replay splits it: f[0] the ring sequence, f[1] "Iv", f[2] the owner, f[3] the set.
        public static bool Parse(string[] f, out ulong owner, out List<ulong> items)
        {
            owner = 0uL;
            items = new List<ulong>();
            if (f == null || f.Length < 4 || f[1] != Kind
                || !ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out owner) || owner == 0uL)
            {
                owner = 0uL;
                return false;
            }

            if (f[3] == "-")
            {
                return true;
            }

            string[] parts = f[3].Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                ulong id;
                if (!ulong.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0uL)
                {
                    owner = 0uL;
                    items.Clear();
                    return false;
                }

                items.Add(id);
            }

            return true;
        }

        // What comes out of current and what goes in to make it target, each id once.
        public static void Diff(List<ulong> current, List<ulong> target, List<ulong> remove, List<ulong> add)
        {
            Dictionary<ulong, bool> want = new Dictionary<ulong, bool>();
            for (int i = 0; i < target.Count; i++)
            {
                want[target[i]] = true;
            }

            Dictionary<ulong, bool> have = new Dictionary<ulong, bool>();
            for (int i = 0; i < current.Count; i++)
            {
                if (have.ContainsKey(current[i]))
                {
                    continue;
                }

                have[current[i]] = true;
                if (!want.ContainsKey(current[i]))
                {
                    remove.Add(current[i]);
                }
            }

            Dictionary<ulong, bool> added = new Dictionary<ulong, bool>();
            for (int i = 0; i < target.Count; i++)
            {
                if (!have.ContainsKey(target[i]) && !added.ContainsKey(target[i]))
                {
                    added[target[i]] = true;
                    add.Add(target[i]);
                }
            }
        }
    }
}
