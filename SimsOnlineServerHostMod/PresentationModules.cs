using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // A stream line handler in a presentation module: fields of one line (seq|kind|...); true when applied.
    public delegate bool PresentationLineHandler(string[] fields);

    // A module's teardown (what it built for the last session goes) and its counters for SACS_REPLAY 00.
    public delegate void PresentationReset();
    public delegate string PresentationReport();

    // A module's work on every replay batch (Walks: stopping walks that outlive the server's, the position mirror).
    public delegate void PresentationTick();

    // A module's walk state for this client's copy of a Sim (local object id), flags: WalkRunning (a client walk runs:
    // the copy's animation and posture lines wait for it), WalkHoldsPlace (the position mirrors leave the copy alone),
    // WalkMoving (it is moving on the server's path now: placements wait).
    public delegate int PresentationWalkState(ulong localSim);

    // The client presentation's reloadable part (scripts/presentation_reload.py, internal documentation). A module is a
    // script assembly with a public static class SimsOnline.Presentation.Module whose Start() registers line handlers by
    // stream kind (Register), a reset (OnReset) and a report (OnReport), using PresentationApi for what the shell owns
    // (object aliases, the presenter). The packaged module (a second S3SA in the mod package) starts with the shell. A
    // reload loads a newer build (its own assembly name: .NET 2 cannot unload, and a repeated name aliases the resident
    // one), resets the old module and the shell's presentation, and switches every kind to the new handlers; the client
    // then rejoins and the late-join history builds the presentation again. Kinds no module claims stay with the shell's
    // built-in replay.
    public static class PresentationModules
    {
        public const string ModuleTypeName = "SimsOnline.Presentation.Module";
        public const string PackagedAssemblyName = "SimsOnlinePresentation";

        private static readonly Dictionary<string, PresentationLineHandler> sHandlers = new Dictionary<string, PresentationLineHandler>();
        private static PresentationReset sReset;
        private static PresentationReport sReport;
        private static PresentationTick sTick;
        private static PresentationWalkState sWalkState;
        private static string sModule = "-";
        private static int sLoads;
        private static string sLastError = "-";

        public static void Register(string kind, PresentationLineHandler handler)
        {
            if (kind != null && handler != null)
            {
                sHandlers[kind] = handler;
            }
        }

        public static void OnReset(PresentationReset reset)
        {
            sReset = reset;
        }

        public static void OnReport(PresentationReport report)
        {
            sReport = report;
        }

        public static void OnTick(PresentationTick tick)
        {
            sTick = tick;
        }

        public static void OnWalkState(PresentationWalkState walkState)
        {
            sWalkState = walkState;
        }

        public const int WalkRunning = 1;
        public const int WalkHoldsPlace = 2;
        public const int WalkMoving = 4;

        internal static bool HasWalks
        {
            get { return sWalkState != null; }
        }

        internal static int WalkState(ulong localSim)
        {
            try
            {
                return sWalkState == null ? 0 : sWalkState(localSim);
            }
            catch (Exception ex)
            {
                sLastError = "walkState:" + ex.GetType().Name;
                return 0;
            }
        }

        internal static void Tick()
        {
            try
            {
                if (sTick != null)
                {
                    sTick();
                }
            }
            catch (Exception ex)
            {
                sLastError = "tick:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        internal static PresentationLineHandler HandlerFor(string kind)
        {
            PresentationLineHandler handler;
            return sHandlers.Count > 0 && sHandlers.TryGetValue(kind, out handler) ? handler : null;
        }

        // The active module's teardown (its own state); the shell's ResetPresentation is separate.
        internal static void ResetModule()
        {
            try
            {
                if (sReset != null)
                {
                    sReset();
                }
            }
            catch (Exception ex)
            {
                sLastError = "reset:" + ex.GetType().Name;
            }
        }

        // At startup: the module packaged with the shell, if the package has one.
        internal static string StartPackaged()
        {
            if (sLoads > 0)
            {
                return "module=" + sModule;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == PackagedAssemblyName)
                {
                    return Start(assembly);
                }
            }

            return "module=none";
        }

        // A reload: a newer module build, from bytes uploaded to the bridge (DIAG_BLOB).
        internal static string Load(byte[] bytes)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(bytes);
            }
            catch (Exception ex)
            {
                sLastError = "load:" + ex.GetType().Name + ":" + ex.Message;
                return "error=" + sLastError;
            }

            return Start(assembly);
        }

        private static string Start(Assembly assembly)
        {
            Type type = assembly.GetType(ModuleTypeName);
            MethodInfo start = type == null ? null : type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            if (start == null)
            {
                sLastError = "no " + ModuleTypeName + ".Start() in " + assembly.GetName().Name;
                return "error=" + sLastError;
            }

            // The old module's state goes with it; the shell's presentation is reset by the caller (a rejoin follows).
            ResetModule();
            Dictionary<string, PresentationLineHandler> previous = new Dictionary<string, PresentationLineHandler>(sHandlers);
            PresentationReset previousReset = sReset;
            PresentationReport previousReport = sReport;
            PresentationTick previousTick = sTick;
            PresentationWalkState previousWalkState = sWalkState;
            sHandlers.Clear();
            sReset = null;
            sReport = null;
            sTick = null;
            sWalkState = null;
            try
            {
                start.Invoke(null, null);
            }
            catch (Exception ex)
            {
                sHandlers.Clear();
                foreach (KeyValuePair<string, PresentationLineHandler> entry in previous)
                {
                    sHandlers[entry.Key] = entry.Value; // a module that fails to start leaves the last one in place
                }

                sReset = previousReset;
                sReport = previousReport;
                sTick = previousTick;
                sWalkState = previousWalkState;

                Exception inner = ex.InnerException ?? ex;
                sLastError = "start:" + inner.GetType().Name + ":" + inner.Message;
                return "error=" + sLastError;
            }

            sModule = assembly.GetName().Name;
            sLoads++;
            List<string> kinds = new List<string>(sHandlers.Keys);
            kinds.Sort(StringComparer.Ordinal);
            return "module=" + sModule + ";kinds=" + string.Join(",", kinds.ToArray());
        }

        internal static string Report()
        {
            string report = "-";
            try
            {
                if (sReport != null)
                {
                    report = sReport();
                }
            }
            catch (Exception ex)
            {
                report = "report-error:" + ex.GetType().Name;
            }

            return "module=" + sModule + "/loads:" + sLoads.ToString(CultureInfo.InvariantCulture) + "/kinds:" + sHandlers.Count.ToString(CultureInfo.InvariantCulture)
                + "/lastError:" + sLastError.Replace(';', ',').Replace('|', '/') + "/" + report;
        }
    }

    // What a presentation module may use of the shell. Grows as kinds move into the module; keep it small and stable.
    public static class PresentationApi
    {
        // This client's object for a server object id (its copy, or the same object under the same id).
        public static ObjectGuid ReplayObject(ulong serverId)
        {
            return ServerHostMain.ReplayObjectForModules(serverId);
        }

        // Pair a server object this client has no object for with this client's own object (an inventory item the server
        // made at runtime): ReplayObject maps it from then on. False when either side is already mapped.
        public static bool MatchObject(ulong serverId, ObjectGuid local)
        {
            return ServerHostMain.MatchItemForModules(serverId, local);
        }

        // The fork's PresenterHook type when presenter mode is on (its display fields and delegates), else null.
        public static Type PresenterHook()
        {
            return ServerHostMain.PresenterHookForModules();
        }

        public static bool PresenterOn()
        {
            return ServerHostMain.PresenterOn();
        }

        // A puppet the server has stood up: its copy takes the standing posture (presenter mode).
        public static void StandPuppet(ulong localSim)
        {
            ServerHostMain.PresenterApplyForModules(localSim, "StandingPosture");
        }

        // The copy's own machines left dead by the replay (standing idle, the route bridge, the facial idle) start again.
        // Its one caller is the module's client walk (Walks.RunClientRoutes), so the label is set here, in the shell: a
        // module built against a new overload would fail its whole walk method on an older shell (presentation_reload).
        public static void RepairOwnMachines(ObjectGuid localSim)
        {
            string context = SacsRecorder.LocalContext;
            SacsRecorder.LocalContext = RepairLabel.Walk;
            try
            {
                ServerHostMain.RepairOwnMachines(localSim);
            }
            finally
            {
                SacsRecorder.LocalContext = context;
            }
        }

        public static bool RepairOrphanedSocializingPosture(Sim localSim)
        {
            return ServerHostMain.RepairOrphanedSocializingPosture(localSim);
        }

        // The stream lines held behind this copy's walk (its animation, posture and stop lines) play now, in order.
        public static void ReleaseHeldLines(ulong localSim)
        {
            ServerHostMain.ReleaseHeldLines(localSim);
        }

        // The server's Sim (by this client's copy) as its posture lines (Po) last said.
        public static bool ServerSimOnObject(ulong localSim)
        {
            return ServerHostMain.ServerSimInContainerPosture(localSim);
        }

        // On a seat with a containment slot: the fork's seat settle puts the copy on that slot.
        public static bool ServerSimOnSeat(ulong localSim)
        {
            return ServerHostMain.ServerSimOnSeat(localSim);
        }

        public static bool ServerSimStanding(ulong localSim)
        {
            return ServerHostMain.ServerSimIsStanding(localSim);
        }

        public static bool ServerSimRecentlyPostured(ulong localSim)
        {
            return ServerHostMain.RecentlyPostured(localSim);
        }

        // When the stream last moved one of this copy's interaction machines (an E or R line on a driver the replay
        // acquired, not the copy's own idles); DateTime.MinValue when never.
        public static DateTime StreamAnimatedUtc(ulong localSim)
        {
            return ServerHostMain.StreamAnimatedUtc(localSim);
        }

        // When this copy's late-join history last began (its seq-0 StopAllAnimation); DateTime.MinValue when never.
        public static DateTime JoinResetUtc(ulong localSim)
        {
            return ServerHostMain.JoinResetUtc(localSim);
        }

        // The stream positions this copy (Pz): the snapshot position mirror leaves it alone for a while.
        public static void MarkStreamPositioned(ulong localSim)
        {
            ServerHostMain.MarkStreamPositioned(localSim);
        }

        // The mod path running now, for the recorders and the motion watch ("mirror Pz", "walk end snap"); null = the game.
        public static string LocalContext
        {
            get { return SacsRecorder.LocalContext; }
            set { SacsRecorder.LocalContext = value; }
        }

        // An event on a watched object (scripts/motion_watch.py).
        public static void Note(ulong obj, string what)
        {
            MotionWatch.Note(obj, what);
        }
    }
}
