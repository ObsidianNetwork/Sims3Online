"""Inspect native routability, terrain height and room/lot identity without moving a Sim.

Uses the same CreateRoute().IsPointRoutable test as the native player's terrain
definition (Terrain routing definition). Creates and disposes a temporary route; never
plans or executes it. Compare supplied and terrain-snapped points before a click.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import subprocess
import sys

try:
    from . import probe_source
    from .local_endpoints import SERVER_BRIDGECTL
except ImportError:
    import probe_source
    from local_endpoints import SERVER_BRIDGECTL

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = r'''using System;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
namespace SimsOnline.Scratch
{
    public class RoutePointsProbe
    {
        public static string Run()
        {
            StringBuilder output = new StringBuilder();
            try
            {
                string requested = __SIM__;
                Sim actor = null;
                foreach (Sim candidate in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (candidate == null || candidate.SimDescription == null) continue;
                    if (candidate.ObjectId.Value.ToString() != requested
                        && !string.Equals(candidate.FullName, requested, StringComparison.OrdinalIgnoreCase)
                        && !candidate.FullName.StartsWith(requested + " ", StringComparison.OrdinalIgnoreCase)) continue;
                    if (actor != null) return "error=ambiguous Sim; use object ID";
                    actor = candidate;
                }
                if (actor == null) return "error=Sim not found";
                output.Append("actor=").Append(actor.ObjectId.Value).Append("|position=").Append(actor.Position)
                    .Append("|room=").Append(World.GetRoomId(actor.Position)).Append('\n');
                Vector3[] points = new Vector3[] { __POINTS__ };
                Route route = actor.CreateRoute();
                try
                {
                    foreach (Vector3 point in points)
                    {
                        float terrain = World.GetTerrainHeight(point.x, point.z);
                        Vector3 snapped = point;
                        if (Math.Abs(point.y - terrain) > 0.5f) snapped.y = terrain;
                        Lot lot = LotManager.GetLotAtPoint(point);
                        output.Append("point=").Append(point).Append("|routable=").Append(route.IsPointRoutable(point))
                            .Append("|room=").Append(World.GetRoomId(point)).Append("|terrain=").Append(terrain)
                            .Append("|snapped=").Append(snapped).Append("|snappedRoutable=").Append(route.IsPointRoutable(snapped))
                            .Append("|snappedRoom=").Append(World.GetRoomId(snapped))
                            .Append("|lot=").Append(lot == null ? "none" : lot.LotId.ToString()).Append('\n');
                    }
                }
                finally { route.Dispose(); }
            }
            catch (Exception error) { output.Append("error=").Append(error.GetType().Name).Append('|').Append(error.Message); }
            return output.ToString();
        }
    }
}
'''


def source(sim: str, points: list[list[float]]) -> str:
    if not sim or len(sim) > 80 or any(ord(c) < 32 for c in sim):
        raise ValueError("Sim must be a name or object ID of at most 80 characters")
    if not 1 <= len(points) <= 64 or any(len(p) != 3 or any(not math.isfinite(v) or abs(v) > 1e6 for v in p) for p in points):
        raise ValueError("provide 1..64 finite XYZ points within +/-1000000")
    vectors = ",".join("new Vector3(" + ",".join(format(v, ".9g") + "f" for v in point) + ")" for point in points)
    return TEMPLATE.replace("__SIM__", json.dumps(sim, ensure_ascii=True)).replace("__POINTS__", vectors)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sim", help="unique first/full name or native object ID")
    parser.add_argument("--point", type=float, nargs=3, action="append", required=True, metavar=("X", "Y", "Z"))
    parser.add_argument("--endpoint", default=SERVER_BRIDGECTL)
    parser.add_argument("--render-only", action="store_true")
    args = parser.parse_args(argv)
    try:
        text = source(args.sim, args.point)
    except ValueError as error:
        parser.error(str(error))
    probe = probe_source.write(ROOT / ".omo/run/act/RoutePointsProbe.cs", text)
    if args.render_only:
        print(probe)
        return 0
    result = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", args.endpoint, "probe", str(probe.relative_to(ROOT))],
                            cwd=ROOT, stdin=subprocess.DEVNULL, capture_output=True, encoding="utf-8", errors="replace", timeout=240)
    output = result.stdout + result.stderr
    print(output)
    if result.returncode != 0:
        return result.returncode
    if any(line.lstrip().startswith("error=") for line in output.splitlines()):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
