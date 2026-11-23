"""Look at the two TS3 windows only (server | client): focus, follow, capture, dismiss the startup notice.

    python scripts/ts3_view.py capture --focus Jennifer --pairs 3 --interval 1.5
    python scripts/ts3_view.py capture --focus Pablo --crop full
    python scripts/ts3_view.py focus Pablo
    python scripts/ts3_view.py follow Pablo              # the game's own follow on both games, until unfollow
    python scripts/ts3_view.py follow Pablo --check      # is each camera still following Pablo?
    python scripts/ts3_view.py unfollow
    python scripts/ts3_view.py dismiss
    python scripts/ts3_view.py capture --client 2 --focus Pablo   # server | player 2's game
    python scripts/ts3_view.py focus Jennifer --at 1139.3,984.8 --games server,1,2
    python scripts/ts3_view.py camera --games server,1,2  # where each camera is and looks, each Sim's distance

capture grabs each TS3 window's own content (PrintWindow: works when other windows cover it, and never captures
anything else on the desktop), pairs server | client at the same moment, and stacks the pairs into one image with a
label per frame. --focus first points both games' cameras at that Sim (a generated probe on each side), so the Sim is
at the centre of both frames and the default crop (centre) keeps the frames small enough to read. Writes
.omo/run/captures/<time>-<focus>.png (or --out) and prints the path.

focus frames a Sim once (a camera lerp). follow locks each camera onto the Sim with the game's own follow, the
portrait's "follow this Sim" (HudModel.AttachCameraToSim -> Camera.LockToSim: FocusOnSim, then
CameraController.EnableObjectFollow on the Sim's object id), so the camera moves with the Sim smoothly until unfollow
(Camera.UnlockFromSim). The game drops a follow by itself: a rabbit hole, a lot change, travel, someone moving the
camera, a copy respawned with a new object id. follow --check reads CameraController.GetObjectFollowId against the
Sim's id on each game: held, relock (the follow was lost), away (the Sim is out of the world or in a rabbit hole:
nothing to follow) or error. motion_watch --capture keeps the follow for its whole watch (2026-10-18).

--client 1|2 (or SIMSONLINE_COMPARE_CLIENT, as for motion_watch and sim_compare) chooses the client game paired with the
server. Each window is matched to its game by process id (the run folder's state and pid files, crash_catch.live_games):
both clients run boxed, so both titles carry Sandboxie's [#] marker. Without those pids the client is the boxed window.

camera reads each game's camera without moving it (CameraController: GetPosition, GetTarget, GetLODInterestPosition,
GetZoom) and each named Sim's distance from the LOD interest point and from the camera. A Sim out of a game's view
walks a short route at full speed from its first step on that game (native: internal documentation),
so a walk-pace difference between two games starts with where their cameras are. --games server,1,2 (focus and camera) names the games instead of the
server and its paired client.

dismiss looks for the game's "Mod Scripts Found" notice (a pale-blue panel over the window's centre) and posts Enter to
each window that shows it, until none does (a notice that appears after the first Enter gets the next one).
"""
from __future__ import annotations

class _MirrorProcFallback:
    GAME_STATE = {"server": "server.state.json", "client": "client.state.json", "client2": "client2.state.json", "client3": "client3.state.json"}

    @staticmethod
    def parse_utc(text):
        from datetime import datetime
        if not text:
            return None
        try:
            return datetime.fromisoformat(str(text).replace("Z", "+00:00")).timestamp()
        except ValueError:
            return None

    @staticmethod
    def attached(state):
        return state.get("mode") == "attach" or any(
            isinstance(h, dict) and h.get("phase") == "host-starting" and h.get("detail") == "attach"
            for h in (state.get("history") or ())
        )

    @classmethod
    def game_refusal(cls, pid, started, identity=None):
        info = identity(pid) if identity else None
        if info is None:
            return "not running"
        image = info[0].replace("\\", "/").rsplit("/", 1)[-1]
        if image.lower() != "ts3w.exe":
            return f"{image}, not TS3W.exe"
        if started is not None and info[1] < started - 5:
            return "created before its controller started (a reused pid)"
        return None

    @classmethod
    def pick_game(cls, side, state, file_pid, identity=None):
        started = None if cls.attached(state) else cls.parse_utc(state.get("startedUtc"))
        refused = []
        for pid, source in ((state.get("gamePid"), f"{cls.GAME_STATE[side]} gamePid"), (file_pid, f"{side}-ts3.pid")):
            if not isinstance(pid, int) or pid <= 0:
                continue
            why = cls.game_refusal(pid, started, identity)
            if why:
                refused.append(f"{source} {pid}: {why}")
            else:
                return {"pid": pid, "source": source, "refused": refused}
        return {"pid": None, "source": None, "refused": refused} if refused else None

    @classmethod
    def live_games(cls, run_dir=None, identity=None):
        import json as _json
        from pathlib import Path as _Path
        run_dir = _Path(run_dir) if run_dir else (_Path(__file__).resolve().parent.parent / ".omo" / "run")
        found = {}
        for side, name in cls.GAME_STATE.items():
            try:
                state = _json.loads((run_dir / name).read_text(encoding="utf-8"))
            except (OSError, ValueError):
                state = {}
            try:
                file_pid = int((run_dir / f"{side}-ts3.pid").read_text(encoding="utf-8").strip())
            except (OSError, ValueError):
                file_pid = None
            game = cls.pick_game(side, state, file_pid, identity)
            if game is not None:
                found[side] = game
        return found

    @staticmethod
    def alive(pid):
        import ctypes
        import os as _os
        import sys as _sys
        if not pid or pid <= 0:
            return False
        if _sys.platform == "win32":
            h = ctypes.windll.kernel32.OpenProcess(0x1000 | 0x00100000, False, int(pid))
            if not h:
                return False
            try:
                return ctypes.windll.kernel32.WaitForSingleObject(h, 0) != 0
            finally:
                ctypes.windll.kernel32.CloseHandle(h)
        try:
            _os.kill(int(pid), 0)
            return True
        except OSError:
            return False

    @classmethod
    def game_pids(cls, run_dir=None):
        return {side: g["pid"] for side, g in cls.live_games(run_dir).items() if g.get("pid") and side in ("server", "client")}

    @staticmethod
    def debugger_kinds():
        return {}

    @staticmethod
    def wait_for_dumps(*args, **kwargs):
        return {}

    @staticmethod
    def writing_dumps(*args, **kwargs):
        return []

    @staticmethod
    def crash_reports(*args, **kwargs):
        return []

    @staticmethod
    def describe_report(*args, **kwargs):
        return ""


import argparse
import ctypes
import ctypes.wintypes as wt
import json
import subprocess
import sys
import time
from pathlib import Path

if __package__:
    from .local_endpoints import CLIENT_BRIDGECTL, SERVER_BRIDGECTL
else:
    from local_endpoints import CLIENT_BRIDGECTL, SERVER_BRIDGECTL

ROOT = Path(__file__).resolve().parent.parent
ENDPOINTS = {"server": SERVER_BRIDGECTL, "client": CLIENT_BRIDGECTL}


def endpoints() -> dict[str, str]:
    """The server and the client game paired with it (compare_client: --client or SIMSONLINE_COMPARE_CLIENT)."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import compare_client
    return {"server": ENDPOINTS["server"], "client": compare_client.endpoint()}


def games(spec: str | None = None) -> dict[str, str]:
    """The games a camera command acts on: the server and its paired client (endpoints()) without a spec, else each
    of a comma list of server, 1, 2 or HOST:PORT, labelled server, client 1, client 2."""
    if not spec:
        return endpoints()
    sys.path.insert(0, str(ROOT / "scripts"))
    import compare_client
    chosen: dict[str, str] = {}
    for part in (p.strip() for p in spec.split(",") if p.strip()):
        if part == "server":
            chosen["server"] = ENDPOINTS["server"]
        else:
            endpoint = compare_client.resolve(part)
            chosen[compare_client.name(endpoint)] = endpoint
    return chosen


def client_side() -> str:
    """The run folder's name for the paired client game: client, or client<N> for player N's (2, 3)."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import compare_client
    player = compare_client.player_of(compare_client.endpoint())
    return "client" if player in (None, "1") else "client" + player
FOCUS_PROBE = ROOT / ".omo" / "run" / "act" / "CameraFocusSimProbe.cs"
FOLLOW_PROBE = ROOT / ".omo" / "run" / "act" / "CameraFollowSimProbe.cs"
FOLLOW_MODES = ("lock", "unlock", "check")

FOCUS_TEMPLATE = r'''using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Generated by scripts/ts3_view.py: this TS3's camera above and behind the named Sim, looking at it (or at a
    // spot on the Sim's level, focus --at).
    public class CameraFocusSimProbe
    {
        public static string Run()
        {
            Sim found = null;
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (sim.SimDescription != null && string.Equals(sim.FirstName, "__NAME__", StringComparison.OrdinalIgnoreCase))
                {
                    found = sim;
                    if (sim.Household != null && sim.Household == Household.ActiveHousehold)
                    {
                        break;
                    }
                }
            }

            if (found == null)
            {
                return "ok=false;err=no Sim named __NAME__";
            }

            Vector3 at = __AT__;
            CameraController.RequestLerpToPositionAndTarget(new Vector3(at.x - __DX__f, at.y + __DY__f, at.z - __DZ__f), at, 0.3f, false);
            return "ok=true;focused=" + __FOCUSED__;
        }
    }
}
'''

# One template for the three modes; a mode and name give one fixed source, so bridgectl compiles each once
# (probe_source names the file by content, cached_probe keys the DLL by it) and the watchdog's checks reuse it.
FOLLOW_TEMPLATE = r'''using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Generated by scripts/ts3_view.py: this TS3's own camera follow on the named Sim, the portrait's
    // "follow this Sim" (HudModel.AttachCameraToSim -> Camera.LockToSim). Mode: lock, unlock or check.
    public class CameraFollowSimProbe
    {
        public static string Run()
        {
            string mode = "__MODE__";
            if (mode == "unlock")
            {
                ulong was = CameraController.GetObjectFollowId();
                Sims3.Gameplay.Core.Camera.UnlockFromSim();
                return "ok=true;mode=unlock;was=" + was + ";followId=" + CameraController.GetObjectFollowId();
            }

            Sim found = null;
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (sim.SimDescription != null && string.Equals(sim.FirstName, "__NAME__", StringComparison.OrdinalIgnoreCase))
                {
                    found = sim;
                    if (sim.Household != null && sim.Household == Household.ActiveHousehold)
                    {
                        break;
                    }
                }
            }

            if (found == null)
            {
                return "ok=false;mode=" + mode + ";err=no Sim named __NAME__;followId=" + CameraController.GetObjectFollowId();
            }

            // Away: nothing to follow (Camera.FocusOnSim returns early or frames the rabbit hole instead), so no lock.
            bool away = !found.InWorld || found.RabbitHoleCurrent != null || found.Position == Vector3.Invalid || found.Position == Vector3.Zero;
            if (mode == "lock" && !away)
            {
                Sims3.Gameplay.Core.Camera.LockToSim(found);
            }

            return "ok=true;mode=" + mode + ";sim=" + found.FullName.Replace(' ', '_') + ";simId=" + found.ObjectId.Value
                + ";followId=" + CameraController.GetObjectFollowId() + ";away=" + (away ? "1" : "0");
        }
    }
}
'''


def _user32():
    user32 = ctypes.windll.user32
    user32.SetProcessDPIAware()
    return user32


def label_windows(found: list[tuple[int, int, str]], games: dict[str, dict], side: str) -> dict[str, int]:
    """{'server': hwnd, 'client': hwnd} from each visible TS3 window's (hwnd, pid, title). A side whose game pid the run
    folder knows (games: crash_catch.live_games) gets the window of that process. One it does not know falls back to the
    title: the server's is the unboxed window, client 1's the boxed one (Sandboxie's [#] marker). Client 2 has no
    fallback: its title looks like client 1's."""
    labelled: dict[str, int] = {}
    for name, role in (("server", "server"), ("client", side)):
        pid = (games.get(role) or {}).get("pid")
        if pid:
            hwnd = next((h for h, p, _ in found if p == pid), None)
        elif role not in ("server", "client"):  # a later player's (client2, client3): no title fallback
            hwnd = None
        else:
            hwnd = next((h for h, _, title in found if ("[#]" in title) == (name == "client")), None)
        if hwnd is not None:
            labelled[name] = hwnd
    return labelled


def windows() -> dict[str, int]:
    """{'server': hwnd, 'client': hwnd}: the visible TS3 windows of the server and the paired client game."""
    user32 = _user32()
    found: list[tuple[int, int, str]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def collect(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        if n and user32.IsWindowVisible(hwnd):
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if "The Sims" in buf.value and "3" in buf.value:
                pid = wt.DWORD()
                user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                found.append((hwnd, pid.value, buf.value))
        return True

    user32.EnumWindows(collect, 0)
    sys.path.insert(0, str(ROOT / "scripts"))
    try:
        import crash_catch
    except ImportError:
        crash_catch = _MirrorProcFallback
    try:
        games = crash_catch.live_games()
    except Exception:  # no run folder, an unreadable state file: the title rule
        games = {}
    return label_windows(found, games, client_side())


class _BMI(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG), ("biPlanes", wt.WORD), ("biBitCount", wt.WORD),
                ("biCompression", wt.DWORD), ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
                ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


def grab(hwnd: int):
    from PIL import Image
    user32, gdi32 = _user32(), ctypes.windll.gdi32
    r = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    hdc = user32.GetWindowDC(hwnd)
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mdc, bmp)
    user32.PrintWindow(hwnd, mdc, 2)
    bi = _BMI()
    bi.biSize, bi.biWidth, bi.biHeight, bi.biPlanes, bi.biBitCount = ctypes.sizeof(_BMI), w, -h, 1, 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(hwnd, hdc)
    return Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0, 1)


def focus_source(name: str, distance: float = 1.0, at: tuple[float, float] | None = None) -> str:
    """The focus probe: the camera above and behind the Sim, looking at it; with at=(x, z), looking at that spot on
    the Sim's level instead (a view with no Sim in it)."""
    point = "found.Position" if at is None else f"new Vector3({at[0]:.2f}f, found.Position.y, {at[1]:.2f}f)"
    shown = "found.FullName.Replace(' ', '_')" if at is None else f'"{at[0]:.2f},{at[1]:.2f}"'
    return FOCUS_TEMPLATE.replace("__AT__", point).replace("__FOCUSED__", shown).replace("__NAME__", name) \
        .replace("__DX__", f"{4 * distance:.1f}").replace("__DY__", f"{6 * distance:.1f}").replace("__DZ__", f"{4 * distance:.1f}")


def focus(name: str, distance: float = 1.0, at: tuple[float, float] | None = None,
          on: dict[str, str] | None = None) -> list[str]:
    """Each game's camera on the Sim, or on a spot (focus_source). Where a game's camera looks changes how that game
    walks a Sim: one out of its view walks a short route at full speed from the first step (5.7 cm a tick over 22
    ticks, against 3.2 over 49 in view). After sleep-bed's watch followed Pablo to the bedroom, the server and client 1
    walked Jennifer that way while client 2 did not, and two-hud failed 2 of 2; with both cameras back on Jennifer it
    passed 2 of 2, and with both on the empty bedroom (--at) it failed again (2026-10-21). Natively each route follower
    tests every frame whether a sphere around its Sim meets a camera's frustum within a range that depends on the Sim's
    bounds (internal documentation, static), so --distance 6 (a 50 m lerp, passed 2 of 2) settles
    view against distance only with the camera read back (camera). on: the games (games()); the server and its paired
    client by default."""
    source = focus_source(name, distance, at)
    sys.path.insert(0, str(ROOT / "scripts"))
    import probe_source
    probe = probe_source.write(FOCUS_PROBE, source)  # its own file: a concurrent call cannot overwrite it before it compiles
    results = []
    for side, endpoint in (on or endpoints()).items():  # one after the other: two compiles of a new source at once collide
        out = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoint, "probe", str(probe.relative_to(ROOT))],
                             cwd=ROOT, capture_output=True, text=True, timeout=180).stdout
        line = next((ln.strip() for ln in out.splitlines() if "focused" in ln or "err" in ln), "no reply")
        results.append(f"{side}: {line}")
    time.sleep(0.8)  # the camera lerp
    return results


CAMERA_PROBE = ROOT / ".omo" / "run" / "act" / "CameraReadProbe.cs"
CAMERA_TEMPLATE = r'''using System;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace SimsOnline.Scratch
{
    // Generated by scripts/ts3_view.py: where this TS3's camera is and looks, read only, and how far each named Sim is
    // from its LOD interest point and from the camera.
    public class CameraReadProbe
    {
        static string V(Vector3 v)
        {
            return v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2");
        }

        static string D(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, y = a.y - b.y, z = a.z - b.z;
            return Math.Sqrt(x * x + y * y + z * z).ToString("F1");
        }

        public static string Run()
        {
            Vector3 position = CameraController.GetPosition();
            Vector3 target = CameraController.GetTarget();
            Vector3 lod = CameraController.GetLODInterestPosition();
            string sims = "";
            foreach (Sim sim in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (sim.SimDescription == null || Array.IndexOf(new string[] { __NAMES__ }, sim.FirstName) < 0)
                {
                    continue;
                }

                sims += (sims.Length > 0 ? " " : "") + sim.FirstName + "@" + D(sim.Position, lod) + "/" + D(sim.Position, position);
            }

            return "ok=true;position=" + V(position) + ";target=" + V(target) + ";lod=" + V(lod)
                + ";zoom=" + CameraController.GetZoom().ToString("F2") + ";sims=" + (sims.Length > 0 ? sims : "-");
        }
    }
}
'''


def camera_source(names: list[str]) -> str:
    """The camera read probe for these Sims' first names."""
    return CAMERA_TEMPLATE.replace("__NAMES__", ", ".join(json.dumps(n) for n in names))


def camera_read(names: list[str], on: dict[str, str] | None = None, timeout: float = 180) -> dict[str, dict[str, str]]:
    """Each game's camera, read only: {game: the probe's fields (position, target, lod, zoom, sims)}."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import probe_source
    probe = probe_source.write(CAMERA_PROBE, camera_source(names))
    results: dict[str, dict[str, str]] = {}
    for side, endpoint in (on or endpoints()).items():
        try:
            done = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoint, "--json", "probe",
                                   str(probe.relative_to(ROOT))], cwd=ROOT, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            results[side] = {"err": f"no reply in {timeout:.0f}s"}
            continue
        results[side] = parse_reply(done.stdout or "")
    return results


def describe_camera(results: dict[str, dict[str, str]]) -> list[str]:
    """One line per game: the camera's position, target, LOD interest point and zoom, then each Sim's distances."""
    lines = []
    for side, fields in results.items():
        if "err" in fields:
            lines.append(f"{side}: error ({fields['err']})")
            continue
        sims = []
        for entry in fields.get("sims", "-").split():
            name, _, far = entry.partition("@")
            lod, _, cam = far.partition("/")
            sims.append(f"{name} {lod} m from the LOD point, {cam} m from the camera")
        lines.append(f"{side}: camera at {fields.get('position', '?')} looking at {fields.get('target', '?')}, LOD point "
                     f"{fields.get('lod', '?')}, zoom {fields.get('zoom', '?')}" + ("; " + "; ".join(sims) if sims else ""))
    return lines


def follow_source(mode: str, name: str = "") -> str:
    """The follow probe's C# for one mode (lock, unlock, check) and Sim; unlock needs no name."""
    if mode not in FOLLOW_MODES:
        raise ValueError(f"camera follow mode {mode!r}: one of {', '.join(FOLLOW_MODES)}")
    return FOLLOW_TEMPLATE.replace("__MODE__", mode).replace("__NAME__", "" if mode == "unlock" else name)


def parse_reply(stdout: str) -> dict[str, str]:
    """A probe's own fields from `bridgectl --json probe` output, whose last JSON line is the reply; {'err': ...} when
    the probe did not run or reported a failure."""
    for line in reversed(stdout.splitlines()):
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            reply = json.loads(line)
        except ValueError:
            return {"err": "unreadable reply: " + line[:120]}
        if not isinstance(reply, dict):
            return {"err": "unreadable reply: " + line[:120]}
        if reply.get("ok") is False:
            return {"err": str(reply.get("error") or "bridge error")}
        probe = reply.get("probe")
        if not isinstance(probe, dict):
            return {"err": f"no probe report (accepted={reply.get('accepted')})"}
        if probe.get("error") or "raw" in probe:
            return {"err": str(probe.get("run") or probe.get("raw") or "probe error").strip()[:200]}
        fields = {str(k): str(v) for k, v in (probe.get("fields") or {}).items()}
        return fields or {"err": "empty probe report"}
    return {"err": "no reply"}


def decide(fields: dict[str, str]) -> str:
    """One game's follow state from a follow probe's fields: held (its camera follows the Sim), relock (it follows
    nothing or something else: the follow was lost), away (the Sim is out of the world or in a rabbit hole, so there is
    nothing to follow and locking again would only repeat) or error (no Sim, no reply)."""
    if fields.get("ok") != "true" or "err" in fields:
        return "error"
    if fields.get("away") == "1":
        return "away"
    sim = fields.get("simId", "")
    return "held" if sim and sim != "0" and fields.get("followId") == sim else "relock"


def camera(mode: str, name: str = "", sides: list[str] | None = None, timeout: float = 180) -> dict[str, dict[str, str]]:
    """Run the follow probe in one mode on each game (or the named sides): {side: its fields}. One side after the other:
    two compiles of one new source at once would write the same DLL."""
    sys.path.insert(0, str(ROOT / "scripts"))
    import probe_source
    probe = probe_source.write(FOLLOW_PROBE, follow_source(mode, name))
    results: dict[str, dict[str, str]] = {}
    for side in sides or list(ENDPOINTS):
        try:
            done = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoints()[side], "--json", "probe",
                                   str(probe.relative_to(ROOT))], cwd=ROOT, capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            results[side] = {"err": f"no reply in {timeout:.0f}s"}
            continue
        fields = parse_reply(done.stdout or "")
        if fields.get("err") == "no reply" and (done.stderr or "").strip():
            fields["err"] = done.stderr.strip().splitlines()[-1][:200]
        results[side] = fields
    return results


def follow(name: str, sides: list[str] | None = None, timeout: float = 180) -> dict[str, dict[str, str]]:
    """Lock each game's camera onto the Sim with the game's own follow (Camera.LockToSim); not while it is away."""
    return camera("lock", name, sides, timeout)


def unfollow(sides: list[str] | None = None, timeout: float = 180) -> dict[str, dict[str, str]]:
    """Release each game's camera (Camera.UnlockFromSim)."""
    return camera("unlock", "", sides, timeout)


def follow_state(name: str, sides: list[str] | None = None, timeout: float = 180) -> dict[str, dict[str, str]]:
    """Each game's follow against the Sim, read only (CameraController.GetObjectFollowId); decide() names it."""
    return camera("check", name, sides, timeout)


def describe_follow(results: dict[str, dict[str, str]]) -> list[str]:
    lines = []
    for side, fields in results.items():
        if fields.get("mode") == "unlock":
            lines.append(f"{side}: released (was following {fields.get('was', '?')}, now {fields.get('followId', '?')})")
            continue
        state = decide(fields)
        detail = fields.get("err") or f"{fields.get('sim', '?')} id {fields.get('simId', '?')}, camera follows {fields.get('followId', '?')}"
        lines.append(f"{side}: {state} ({detail})")
    return lines


def _crop(img, crop: str):
    if crop == "full":
        return img.resize((img.width // 2, img.height // 2))
    if crop == "centre":
        w, h = img.size
        cw, ch = int(w * 0.5), int(h * 0.75)
        x, y = (w - cw) // 2, int(h * 0.12)
        return img.crop((x, y, x + cw, y + ch))
    x, y, cw, ch = (int(v) for v in crop.split(","))
    return img.crop((x, y, x + cw, y + ch))


def capture(pairs: int, interval: float, crop: str, out: Path, title: str = "") -> Path:
    from PIL import Image, ImageDraw
    wins = windows()
    missing = [side for side in ("server", "client") if side not in wins]
    if missing:
        raise SystemExit(f"TS3 window not found: {', '.join(missing)}")
    names = ("server", "client " + client_side()[6:] if client_side() != "client" else "client")
    rows = []
    start = time.time()
    for i in range(pairs):
        t0 = time.time()
        frames = [_crop(grab(wins["server"]), crop), _crop(grab(wins["client"]), crop)]
        stamp = time.time() - start
        row = Image.new("RGB", (frames[0].width + frames[1].width + 6, frames[0].height + 18), (30, 30, 30))
        draw = ImageDraw.Draw(row)
        for j, (side, frame) in enumerate(zip(names, frames)):
            x = j * (frames[0].width + 6)
            row.paste(frame, (x, 18))
            draw.text((x + 4, 3), f"{side}  t+{stamp:.1f}s{('  ' + title) if title and j == 0 else ''}", fill=(235, 235, 235))
        rows.append(row)
        time.sleep(max(0.0, interval - (time.time() - t0)))
    sheet = Image.new("RGB", (rows[0].width, sum(r.height for r in rows) + 6 * (len(rows) - 1)), (60, 60, 60))
    y = 0
    for r in rows:
        sheet.paste(r, (0, y))
        y += r.height + 6
    out.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out)
    return out


def _notice_showing(img) -> bool:
    """The startup notice: a large pale-blue panel over the window's centre."""
    w, h = img.size
    box = img.crop((int(w * 0.35), int(h * 0.3), int(w * 0.65), int(h * 0.6))).resize((40, 40))
    raw = box.convert("RGB").tobytes()
    pixels = [(raw[i], raw[i + 1], raw[i + 2]) for i in range(0, len(raw), 3)]
    pale_blue = sum(1 for r, g, b in pixels if b > 200 and g > 170 and r > 110 and b > r + 25)
    return pale_blue > len(pixels) * 0.6


def dismiss(tries: int = 8, wait: float = 2.0) -> str:
    user32 = _user32()
    posted = 0
    for attempt in range(tries):
        showing = [hwnd for hwnd in windows().values() if _notice_showing(grab(hwnd))]
        if not showing and attempt == 0:
            showing = list(windows().values())  # one Enter each regardless, in case the check misses the notice
        if not showing:
            return f"startup notice: none showing ({posted} Enter posted)"
        for hwnd in showing:
            user32.PostMessageW(hwnd, 0x100, 0x0D, 0x001C0001)  # WM_KEYDOWN Enter
            time.sleep(0.1)
            user32.PostMessageW(hwnd, 0x101, 0x0D, 0xC01C0001)  # WM_KEYUP
            posted += 1
        time.sleep(wait)
    return f"startup notice: still showing after {posted} Enter"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--client", help="the client game paired with the server: 1 (default), 2 (player 2) or HOST:PORT")
    sub = p.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("capture", parents=[common])
    c.add_argument("--focus", help="first name of the Sim to centre both cameras on first")
    c.add_argument("--distance", type=float, default=1.0, help="camera distance factor for --focus (0.6 closer, 1.5 farther)")
    c.add_argument("--pairs", type=int, default=2)
    c.add_argument("--interval", type=float, default=1.5)
    c.add_argument("--crop", default="centre", help="centre | full | x,y,w,h (window pixels)")
    c.add_argument("--out", help="image path (default .omo/run/captures/<time>-<focus>.png)")
    f = sub.add_parser("focus", parents=[common])
    f.add_argument("name", help="first name of the Sim to look at (with --at: whose level the spot is on)")
    f.add_argument("--distance", type=float, default=1.0)
    f.add_argument("--at", help="x,z: look at this spot instead of the Sim")
    f.add_argument("--games", help="comma list of server, 1, 2 (default: the server and its paired client)")
    cam = sub.add_parser("camera", parents=[common], help="read each game's camera (position, target, LOD point, zoom)")
    cam.add_argument("--sims", default="Jennifer,Pablo", help="first names whose distances to show")
    cam.add_argument("--games", help="comma list of server, 1, 2 (default: the server and its paired client)")
    fo = sub.add_parser("follow", parents=[common], help="lock both cameras onto a Sim with the game's own follow")
    fo.add_argument("name")
    fo.add_argument("--check", action="store_true", help="only read whether each camera still follows the Sim")
    fo.add_argument("--side", choices=list(ENDPOINTS), help="one game only")
    un = sub.add_parser("unfollow", parents=[common], help="release both cameras")
    un.add_argument("--side", choices=list(ENDPOINTS), help="one game only")
    sub.add_parser("dismiss", parents=[common])
    a = p.parse_args(argv)
    if getattr(a, "client", None):
        sys.path.insert(0, str(ROOT / "scripts"))
        import compare_client
        compare_client.use(a.client)

    if a.cmd == "focus":
        spot = None
        if a.at:
            x, z = (float(v) for v in a.at.split(","))
            spot = (x, z)
        print("\n".join(focus(a.name, a.distance, spot, games(a.games))))
        return 0
    if a.cmd == "camera":
        results = camera_read([n for n in a.sims.split(",") if n], games(a.games))
        print("\n".join(describe_camera(results)))
        return 0 if all("err" not in fields for fields in results.values()) else 1
    if a.cmd in ("follow", "unfollow"):
        sides = [a.side] if a.side else None
        if a.cmd == "unfollow":
            results = unfollow(sides)
        else:
            results = follow_state(a.name, sides) if a.check else follow(a.name, sides)
        print("\n".join(describe_follow(results)))
        return 0 if all("err" not in fields for fields in results.values()) else 1
    if a.cmd == "dismiss":
        print(dismiss())
        return 0
    if a.focus:
        for line in focus(a.focus, a.distance):
            print(line)
    out = Path(a.out) if a.out else ROOT / ".omo" / "run" / "captures" / f"{time.strftime('%H%M%S')}-{a.focus or 'view'}.png"
    print(capture(a.pairs, a.interval, a.crop, out, a.focus or ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
