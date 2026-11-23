"""Inventories on the server against a client's: every owner the server watches.

A client's pie menu tests read the client's inventories (a bookshelf offers Read while its copy holds a book). The
server sends the sets it watches (`Iv` lines, ServerHostMain.PublishInventories); this reads both games' sets directly
and prints the owners whose sets differ:

    python scripts/inventory_diff.py --client 2                       # every watched owner, the Sims, objects in 25 m
    python scripts/inventory_diff.py --client 1 --sims Jennifer --radius 8 --all   # every owner, also the agreeing ones
    python scripts/inventory_diff.py --client 2 --no-watched          # only the named Sims and the objects near them

Each side runs one read-only probe. The server's lists the owners the mod watches (any Sim or object, Milton Tong's
inventory too, not only the players'), the named Sims' own inventories, and every object that is not a Sim with an
inventory within --radius of one of them. The client's reads the same owners by the server's ids (through the shell's
replay map) and the named Sims and objects near them. Both name owners and items by the server's ids (the shell's
WireObjectId), so an item the client paired with a runtime item of the server's compares equal. An owner present on
one side only is listed too. Exit 0 when every owner agrees, 3 when some differ, 1 when a side listed nothing.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import compare_client  # noqa: E402
import probe_source  # noqa: E402
import scenarios  # noqa: E402

PROBE = ROOT / ".omo" / "run" / "act" / "InventorySetsProbe.cs"

TEMPLATE = r"""
using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;

namespace SimsOnline.Scratch
{
    // READ-ONLY: the inventory sets of the mod's watched owners (WATCHED), of the given server ids (OWNERS), of the
    // named Sims and of the objects (not Sims) within the radius of them, named by the server's ids (the shell's maps,
    // by reflection; without the shell, this game's ids), one
    // "inv=Type:id|count|ids ascending" line each (scripts/inventory_diff.py).
    public static class InventorySetsProbe
    {
        private const bool Watched = __WATCHED__;
        private const string Owners = "__OWNERS__";
        private static Type sHost;

        private static Type Host()
        {
            if (sHost == null)
            {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("SimsOnlineServerHost.ServerHostMain");
                    if (t != null) { sHost = t; break; }
                }
            }
            return sHost;
        }

        private static object Call(string method, object arg)
        {
            Type host = Host();
            System.Reflection.MethodInfo m = host == null ? null : host.GetMethod(method,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return m == null ? null : m.Invoke(null, new object[] { arg });
        }

        private static ulong Wire(IGameObject obj)
        {
            try { object id = Call("WireObjectId", obj); if (id != null) return (ulong)id; } catch (Exception) { }
            return obj.ObjectId.Value;
        }

        private static GameObject Local(ulong serverId)
        {
            try
            {
                object local = Call("ReplayObjectForModules", serverId);
                if (local is Sims3.SimIFace.ObjectGuid) return ((Sims3.SimIFace.ObjectGuid)local).ObjectFromId<GameObject>();
            }
            catch (Exception) { }
            return new Sims3.SimIFace.ObjectGuid(serverId).ObjectFromId<GameObject>();
        }

        private static List<ulong> WatchedOwners()
        {
            List<ulong> ids = new List<ulong>();
            try
            {
                Type host = Host();
                System.Reflection.FieldInfo f = host == null ? null : host.GetField("sInventoryWatches",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                System.Collections.IDictionary watches = f == null ? null : f.GetValue(null) as System.Collections.IDictionary;
                if (watches != null)
                {
                    foreach (object key in watches.Keys) ids.Add((ulong)key);
                }
            }
            catch (Exception) { }
            return ids;
        }

        public static string Run()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                string[] names = "__SIMS__".Split(',');
                List<Sim> sims = new List<Sim>();
                foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (sim.SimDescription != null && Array.IndexOf(names, sim.FirstName) >= 0)
                    {
                        sims.Add(sim);
                    }
                }

                Dictionary<ulong, GameObject> owners = new Dictionary<ulong, GameObject>();
                List<ulong> order = new List<ulong>();
                List<ulong> listed = Watched ? WatchedOwners() : new List<ulong>();
                int watched = listed.Count;
                foreach (string part in Owners.Split(','))
                {
                    ulong id;
                    if (part.Length > 0 && ulong.TryParse(part, out id)) listed.Add(id);
                }

                int unresolved = 0;
                for (int i = 0; i < listed.Count; i++)
                {
                    GameObject obj = Local(listed[i]);
                    if (obj == null || obj.Inventory == null) { unresolved++; continue; }
                    if (!owners.ContainsKey(obj.ObjectId.Value))
                    {
                        owners[obj.ObjectId.Value] = obj;
                        order.Add(obj.ObjectId.Value);
                    }
                }

                for (int s = 0; s < sims.Count; s++)
                {
                    if (!owners.ContainsKey(sims[s].ObjectId.Value))
                    {
                        owners[sims[s].ObjectId.Value] = sims[s];
                        order.Add(sims[s].ObjectId.Value);
                    }
                }

                for (int s = 0; s < sims.Count; s++)
                {
                    GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(sims[s].Position, __RADIUS__f);
                    for (int i = 0; near != null && i < near.Length; i++)
                    {
                        GameObject obj = near[i];
                        if (obj == null || obj is Sim || obj.Inventory == null || owners.ContainsKey(obj.ObjectId.Value))
                        {
                            continue;
                        }

                        owners[obj.ObjectId.Value] = obj;
                        order.Add(obj.ObjectId.Value);
                    }
                }

                sb.Append("ok=true;sims=").Append(sims.Count).Append(";watched=").Append(watched).Append(";unresolved=").Append(unresolved)
                    .Append(";owners=").Append(order.Count);
                for (int o = 0; o < order.Count; o++)
                {
                    GameObject owner = owners[order[o]];
                    Inventory inventory = owner.Inventory;
                    List<ulong> ids = new List<ulong>();
                    if (inventory != null)
                    {
                        foreach (IGameObject item in inventory.FindAll<IGameObject>(false))
                        {
                            if (item != null)
                            {
                                ids.Add(Wire(item));
                            }
                        }
                    }

                    ids.Sort();
                    string[] parts = new string[ids.Count];
                    for (int i = 0; i < ids.Count; i++)
                    {
                        parts[i] = ids[i].ToString();
                    }

                    sb.Append("\ninv=").Append(owner.GetType().Name).Append(':').Append(Wire(owner)).Append('|').Append(ids.Count)
                        .Append('|').Append(parts.Length == 0 ? "-" : string.Join(",", parts));
                }
            }
            catch (Exception ex)
            {
                sb.Append("\nerr=").Append(ex.GetType().Name).Append(':').Append(ex.Message);
            }

            return sb.ToString();
        }
    }
}
"""


def parse(text: str) -> dict[str, tuple[str, list[str]]]:
    """The probe's reply: {owner id: (owner type, item ids)}."""
    out: dict[str, tuple[str, list[str]]] = {}
    for line in text.splitlines():
        line = line.strip()
        if not line.startswith("inv="):
            continue
        head, _, rest = line[4:].partition("|")
        type_name, _, owner = head.rpartition(":")
        _, _, items = rest.partition("|")
        out[owner] = (type_name, [] if items in ("", "-") else items.split(","))
    return out


def diff(server: dict[str, tuple[str, list[str]]], client: dict[str, tuple[str, list[str]]]) -> list[dict]:
    """Every owner: {owner, type, agree, server_only, client_only, missing_owner: None | "server" | "client"}."""
    rows = []
    for owner in sorted(set(server) | set(client), key=lambda o: (server.get(o) or client[o])[0] + o):
        s = server.get(owner)
        c = client.get(owner)
        type_name = (s or c)[0]
        if s is None or c is None:
            rows.append({"owner": owner, "type": type_name, "agree": False, "server_only": [], "client_only": [],
                         "missing_owner": "server" if s is None else "client"})
            continue
        s_items, c_items = set(s[1]), set(c[1])
        rows.append({"owner": owner, "type": type_name, "agree": s_items == c_items, "server_only": sorted(s_items - c_items),
                     "client_only": sorted(c_items - s_items), "missing_owner": None})
    return rows


def report(rows: list[dict], show_all: bool) -> list[str]:
    differ = [r for r in rows if not r["agree"]]
    lines = [f"{len(rows)} owners, {len(differ)} differ"]
    for r in rows if show_all else differ:
        name = f"{r['type']}:{r['owner']}"
        if r["missing_owner"]:
            lines.append(f"  {name}: not listed on the {r['missing_owner']} (out of range there, or no such object)")
        elif r["agree"]:
            lines.append(f"  {name}: agree")
        else:
            lines.append(f"  {name}: server only {','.join(r['server_only']) or '-'}; client only {','.join(r['client_only']) or '-'}")
    return lines


def read(endpoint: str, sims: str, radius: float, watched: bool = False, owners: list[str] | None = None) -> str:
    source = (TEMPLATE.replace("__SIMS__", sims).replace("__RADIUS__", f"{radius:.1f}")
              .replace("__WATCHED__", "true" if watched else "false").replace("__OWNERS__", ",".join(owners or [])))
    probe = probe_source.write(PROBE, source)
    _, text = scenarios.python("scripts/bridgectl.py", "--endpoint", endpoint, "probe", str(probe.relative_to(ROOT)), timeout=180)
    return text


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--client", default="1", help="the client game: 1 (default), 2 or HOST:PORT")
    p.add_argument("--sims", default="Jennifer,Pablo", help="first names, comma separated (default Jennifer,Pablo)")
    p.add_argument("--radius", type=float, default=25.0, help="metres around each Sim (default 25, stream.world-radius)")
    p.add_argument("--all", action="store_true", help="list the agreeing owners too")
    p.add_argument("--no-watched", action="store_true", help="skip the owners the mod watches (only the Sims and objects near them)")
    a = p.parse_args(argv)
    sides = {}
    for side, endpoint in (("server", scenarios.SERVER), ("client", compare_client.resolve(a.client))):
        # The client reads the server's owners by their server ids, so an owner out of the client's range is still read.
        owners = sorted(sides["server"]) if side == "client" and not a.no_watched else []
        text = read(endpoint, a.sims, a.radius, watched=side == "server" and not a.no_watched, owners=owners)
        sides[side] = parse(text)
        head = text.strip().splitlines()[0] if text.strip() else ""
        print(f"{side}: {head[:160]}")
        if not sides[side]:
            print(f"no inventories from the {side} ({endpoint}): " + text.strip()[-300:])
            return 1
    rows = diff(sides["server"], sides["client"])
    for line in report(rows, a.all):
        print(line)
    return 3 if any(not r["agree"] for r in rows) else 0


if __name__ == "__main__":
    raise SystemExit(main())
