"""Objects near the Sims on the server against a client's: what the client's own world made, and what never reached it.

A client runs the game's own world code on its copy of the lot. Whatever that code makes is the client's alone, and whatever the server makes reaches a
client only as a stream copy. This reads both games' objects near the named Sims, by the server's ids (the shell's
WireObjectId and replay map), and prints:

    python scripts/object_diff.py --client 2                                  # every type within 25 m of Jennifer and Pablo
    python scripts/object_diff.py --client 2 --types Newspaper --radius 40    # one type, the whole lot
    python scripts/object_diff.py --client 2 --types Newspaper --radius 40 --wait-new Newspaper:300

- client only: an object the client lists whose server id the server has no object for (a save object has the same id
  in both games, a stream copy goes by the server's id), so the client's own world made it;
- no client copy: a server object near the Sims that the client has no object for under its id;
- hidden on the client: a server object the server shows whose client copy is hidden (a prop copy no H line showed).

`runtime` marks an object made since that game loaded (the mod's SimulatorRecorder saw it made). --wait-new
Type:seconds first waits until the server has a new object of that type near the Sims (a delivery, a spawner), then
--settle seconds more for a client's own world to make its own, then compares. Each side runs one read-only probe per
read; a third read asks the server for the client-only candidates by id, so an object only out of the server's range
is not listed. Exit 0 when nothing differs, 3 when something does, 1 when a side's probe failed, 4 when --wait-new saw
no new object in time. The scenario step objectdiff:<1|2>[:<these options>] fails on a nonzero exit.
"""

from __future__ import annotations

import argparse
import re
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import compare_client  # noqa: E402
import probe_source  # noqa: E402
import scenarios  # noqa: E402

PROBE = ROOT / ".omo" / "run" / "act" / "ObjectListProbe.cs"

TEMPLATE = r"""
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // READ-ONLY (scripts/object_diff.py): the objects within Radius of the named Sims (Types: only those type names;
    // empty: every type), each by the server's id (the shell's WireObjectId; this game's id without the shell):
    //   obj=Type|wire|local|x,z|runtime|hidden|name
    // runtime 1: made since this game loaded (the mod's SimulatorRecorder knows its creation); hidden: the World hidden
    // flags. Then, for each server id in Ids, this game's object for it (the shell's replay map, which also finds a
    // hidden copy Queries.GetObjects does not list):
    //   id=serverId|Type or -|local|hidden|inWorld
    public static class ObjectListProbe
    {
        private const string Names = "__SIMS__";
        private const string Types = "__TYPES__";
        private const string Ids = "__IDS__";
        private const float Radius = __RADIUS__f;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Type Find(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(name);
                if (t != null) return t;
            }
            return null;
        }

        private static object Call(string method, object arg)
        {
            Type host = Find("SimsOnlineServerHost.ServerHostMain");
            MethodInfo m = host == null ? null : host.GetMethod(method, Static);
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
                if (local is ObjectGuid) return ((ObjectGuid)local).ObjectFromId<GameObject>();
            }
            catch (Exception) { }
            return new ObjectGuid(serverId).ObjectFromId<GameObject>();
        }

        private static bool Runtime(ulong id)
        {
            try
            {
                Type recorder = Find("SimsOnlineServerHost.SimulatorRecorder");
                MethodInfo m = recorder == null ? null : recorder.GetMethod("TryGetKey", Static);
                return m != null && (bool)m.Invoke(null, new object[] { id, null });
            }
            catch (Exception) { return false; }
        }

        private static bool Wanted(GameObject obj)
        {
            if (Types.Length == 0) return true;
            foreach (string t in Types.Split(','))
            {
                if (string.Compare(t, obj.GetType().Name, true, CultureInfo.InvariantCulture) == 0) return true;
            }
            return false;
        }

        private static string Clean(string s)
        {
            return string.IsNullOrEmpty(s) ? "-" : s.Replace('|', '/').Replace('\n', ' ').Replace(';', ',');
        }

        public static string Run()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            int sims = 0;
            int objects = 0;
            int ids = 0;
            try
            {
                string[] names = Names.Split(',');
                Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
                foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (sim.SimDescription == null || Array.IndexOf(names, sim.FirstName) < 0) continue;
                    sims++;
                    GameObject[] near = Sims3.Gameplay.Queries.GetObjects<GameObject>(sim.Position, Radius);
                    for (int i = 0; near != null && i < near.Length; i++)
                    {
                        GameObject obj = near[i];
                        if (obj == null || seen.ContainsKey(obj.ObjectId.Value) || !Wanted(obj)) continue;
                        seen[obj.ObjectId.Value] = true;
                        objects++;
                        Vector3 p = obj.Position;
                        Sim other = obj as Sim;
                        sb.Append("\nobj=").Append(obj.GetType().Name).Append('|').Append(Wire(obj).ToString(c)).Append('|')
                            .Append(obj.ObjectId.Value.ToString(c)).Append('|').Append(p.x.ToString("0.0", c)).Append(',')
                            .Append(p.z.ToString("0.0", c)).Append('|').Append(Runtime(obj.ObjectId.Value) ? "1" : "0").Append('|')
                            .Append(World.ObjectGetHiddenFlags(obj.ObjectId).ToString(c)).Append('|')
                            .Append(Clean(other == null ? null : other.FullName));
                    }
                }

                foreach (string part in Ids.Split(','))
                {
                    ulong id;
                    if (part.Length == 0 || !ulong.TryParse(part, NumberStyles.None, c, out id)) continue;
                    ids++;
                    GameObject obj = Local(id);
                    sb.Append("\nid=").Append(part).Append('|').Append(obj == null ? "-" : obj.GetType().Name).Append('|')
                        .Append(obj == null ? "0" : obj.ObjectId.Value.ToString(c)).Append('|')
                        .Append(obj == null ? "0" : World.ObjectGetHiddenFlags(obj.ObjectId).ToString(c)).Append('|')
                        .Append(obj != null && obj.InWorld ? "1" : "0");
                }
            }
            catch (Exception ex)
            {
                sb.Append("\nerr=").Append(ex.GetType().Name).Append(':').Append(Clean(ex.Message));
            }

            return "ok=true;sims=" + sims + ";objects=" + objects + ";ids=" + ids + sb.ToString();
        }
    }
}
"""


def source(sims: str, radius: float, types: str = "", ids: list[str] | None = None) -> str:
    return (TEMPLATE.replace("__SIMS__", sims).replace("__RADIUS__", f"{radius:.1f}").replace("__TYPES__", types)
            .replace("__IDS__", ",".join(ids or [])))


def parse(text: str) -> tuple[dict[str, dict], dict[str, dict]] | None:
    """The probe's reply: ({server id: object near the Sims}, {asked server id: this game's object or type None}), or
    None when the probe did not answer. bridgectl prints the reply's first line as aligned "  ok = true" fields and the
    obj=/id= lines below them; the raw "ok=true;..." form is accepted too."""
    if not re.search(r"(?:^|;)\s*ok\s*=\s*true\b", text, re.IGNORECASE | re.MULTILINE):
        return None
    objects: dict[str, dict] = {}
    ids: dict[str, dict] = {}
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("obj="):
            f = line[4:].split("|")
            if len(f) >= 7:
                objects[f[1]] = {"type": f[0], "local": f[2], "pos": f[3], "runtime": f[4] == "1",
                                 "hidden": f[5] not in ("0", ""), "name": f[6]}
        elif line.startswith("id="):
            f = line[3:].split("|")
            if len(f) >= 5:
                ids[f[0]] = {"type": None if f[1] == "-" else f[1], "local": f[2], "hidden": f[3] not in ("0", ""),
                             "in_world": f[4] == "1"}
    return objects, ids


def diff(server: dict[str, dict], client: dict[str, dict], server_has: dict[str, dict], client_has: dict[str, dict]) -> dict:
    """server / client: each side's objects near the Sims by server id; server_has: the server's answer for the client's
    candidates; client_has: the client's answer for the server's objects. Lists of (server id, object)."""
    client_only = [(wire, obj) for wire, obj in sorted(client.items())
                   if wire not in server and (server_has.get(wire) or {}).get("type") is None]
    no_copy = [(wire, obj) for wire, obj in sorted(server.items())
               if wire not in client and (client_has.get(wire) or {}).get("type") is None]
    hidden = [(wire, obj) for wire, obj in sorted(server.items()) if not obj["hidden"]
              and ((client.get(wire) or {}).get("hidden") or (client_has.get(wire) or {}).get("hidden"))]
    return {"client only": client_only, "no client copy": no_copy, "hidden on the client": hidden}


def describe(wire: str, obj: dict) -> str:
    name = f" {obj['name']}" if obj.get("name") not in (None, "-") else ""
    return f"{obj['type']}{name} {wire} at {obj['pos']}" + (" runtime" if obj.get("runtime") else "")


def report(found: dict, server: dict, client: dict) -> list[str]:
    lines = [f"server {len(server)} objects near, client {len(client)}; "
             + ", ".join(f"{kind}: {len(rows)}" for kind, rows in found.items())]
    for kind, rows in found.items():
        for wire, obj in rows[:20]:
            lines.append(f"  {kind}: {describe(wire, obj)}")
        if len(rows) > 20:
            lines.append(f"  {kind}: ... {len(rows) - 20} more")
    return lines


def read(endpoint: str, sims: str, radius: float, types: str = "", ids: list[str] | None = None) -> str:
    probe = probe_source.write(PROBE, source(sims, radius, types, ids))
    _, text = scenarios.python("scripts/bridgectl.py", "--endpoint", endpoint, "probe", str(probe.relative_to(ROOT)), timeout=180)
    return text


def wait_new(sims: str, radius: float, kind: str, seconds: float, interval: float = 5.0) -> list[str]:
    """The server ids of the objects of type kind that appear near the Sims on the server within seconds."""
    first = parse(read(scenarios.SERVER, sims, radius, kind))
    before = set(first[0]) if first else set()
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        time.sleep(interval)
        now = parse(read(scenarios.SERVER, sims, radius, kind))
        new = sorted(set(now[0]) - before) if now else []
        if new:
            return new
    return []


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--client", default="1", help="the client game: 1 (default), 2 or HOST:PORT")
    p.add_argument("--sims", default="Jennifer,Pablo", help="first names, comma separated (default Jennifer,Pablo)")
    p.add_argument("--radius", type=float, default=25.0, help="metres around each Sim (default 25, stream.world-radius)")
    p.add_argument("--types", default="", help="type names, comma separated (default every type)")
    p.add_argument("--wait-new", default="", metavar="TYPE:SECONDS", help="first wait for a new server object of TYPE")
    p.add_argument("--settle", type=float, default=30.0, help="seconds after --wait-new before comparing (default 30)")
    a = p.parse_args(argv)
    if a.wait_new:
        kind, _, seconds = a.wait_new.rpartition(":")
        new = wait_new(a.sims, a.radius, kind, float(seconds))
        if not new:
            print(f"no new {kind} on the server near {a.sims} in {seconds}s")
            return 4
        print(f"new {kind} on the server: {', '.join(new)}; comparing in {a.settle:.0f}s")
        time.sleep(a.settle)
    endpoint = compare_client.resolve(a.client)
    server_text = read(scenarios.SERVER, a.sims, a.radius, a.types)
    server_read = parse(server_text)
    if server_read is None:
        print(f"no answer from the server: {server_text.strip()[-300:]}")
        return 1
    server = server_read[0]
    # The client answers for every server object by id (a hidden copy is not in its near list).
    client_text = read(endpoint, a.sims, a.radius, a.types, sorted(server))
    client_read = parse(client_text)
    if client_read is None:
        print(f"no answer from client {a.client} ({endpoint}): {client_text.strip()[-300:]}")
        return 1
    client, client_has = client_read
    # The client's candidates the server's near list lacks: the server answers for them by id (out of its range only).
    candidates = sorted(set(client) - set(server))
    server_has = (parse(read(scenarios.SERVER, a.sims, a.radius, a.types, candidates)) or ({}, {}))[1] if candidates else {}
    found = diff(server, client, server_has, client_has)
    for line in report(found, server, client):
        print(line)
    return 3 if any(found.values()) else 0


if __name__ == "__main__":
    raise SystemExit(main())
