"""Does a fresh standing idle machine entered past its creation state take the next idle request without a restart? arms: the plan's
section 5 A table, with the kProxyInvalid re-resolve reaching the gate (main's ruling, qoder/b2-probe-fix brief).)

On one game (default client 1), for one standing Sim, the make probe:
  1. reads the standing posture's idle machine: its driver handle, alive or dead, and how many server drivers the
     replay has borrowed onto it (the server's idle driver, B2 step 1);
  2. when that machine is alive, kills it with the same engine call the replay's Z handler uses on a client:
     `SacsRecorder.Current.StopAllAnimation(simId)` (Sims3.SimIFace.ISacsProxy.StopAllAnimation, ScriptCore.SACS.cs:327
     -> SACS_StopAllAnimationImpl, which invalidates the machine's driver handle). Sims3.SimIFace.Animation's static
     StopAllAnimation (what the probe ran until the b2-live lane's run 1) stops only the animation clip and leaves
     `oldValid=1`, so no kProxyInvalid follows and the gate is never consulted. `--no-stop` refuses instead;
  3a. with `--via-replay`, the probe makes no machine of its own: it calls the mod's `RepairOwnMachines` under
     `repair:probe` and lets B2 acquire the `idle` machine, set actor x, put it on the posture, run the
     posture's own SetDefaultIdleAnim/SetIdleCallbacks and enter the stream's state. B2's entry goes through
     `SacsRecorder.EnterState`, which marks it in RepairEntryGate (`NoteRepairEntry`). The probe then reads
     `standing.CurrentStateMachine` as the new machine and subscribes to its StateEntered events from there. The
     repair's own entry is confirmed by `idleRepairStream +1`; the entries in the check's `entered` list are the
     pump's follow-ups (B2's entry's StateEntered fires in the pump, 2-4 ms later, so the first entry in the list is
     B2's). The probe then injects `0|R|<server idle driver>|<fnv x>|<fnv request>|448` through the mod's
     `ApplyReplayBatch`: the R hits the old dead handle, gets kProxyInvalid, and the replay's own re-resolve
     (ServerHostMain.cs:11232-11246) re-adds the line onto the new machine the borrow path found, so `TakeRequest`
     sees the gate's mark.
  3b. without `--via-replay` (the pre-B1 shape, kept for the drop-off 1-frame control arm): re-creates the idle
     machine itself, without the engine's creation entry: acquire `idle` at the engine's priority, bind actor x, put
     it on the posture at once, run the posture's own SetDefaultIdleAnim and SetIdleCallbacks, subscribe to its
     StateEntered events, then enter --enter (standing_idle) under the label `repair:probe/stream`; then request
     --request (standing_idle) with the idle loop's own flags (AllowDuplicateRequest|AllowRequestForCurrentState,
     not yielding: a probe runs on the simulator pump). Runs the mod's RepairOwnMachines for the rest (the route
     bridge, the facial idle the stop killed).

After --window seconds (the game must be running) the check probe reports the StateEntered events since, whether the
machine is alive and still the posture's, and how many server drivers the replay now borrows onto it (the replay's
kProxyInvalid re-resolve found the new machine). With `--via-replay` it also reports `reresolvedAfter`, so the script
can print `reresolvedDelta` (the R's kProxyInvalid re-resolve) alongside `newMapped`. Both probes are generated per
run under .omo/run/act/.

  python scripts/idle_repair_probe.py Jennifer                          # client 1, standing_idle, 1.5 s window
  python scripts/idle_repair_probe.py Iliana --client 127.0.0.1:34031 --enter standing_idle --request standing_idle
  python scripts/idle_repair_probe.py Pablo --no-request --window 3     # the entry alone
  python scripts/idle_repair_probe.py Pablo --ingame-delay-ms 150       # the request 150 ms after the entry, in game
  python scripts/idle_repair_probe.py Pablo --via-replay                # the R arrives through the replay (B2's gate)
  python scripts/idle_repair_probe.py Jennifer --dry-run                # write the probes, touch no game

Verdict PASS: the entry and the request took, the creation state (standing) was never entered, the entered state
was seen (the machine animates), no excess entry occurred in the 1 s window after the request after subtracting
the matching --control receipt's natural entries, and the machine is alive and the posture's.
--via-replay --no-request --window 6 --output control.json records a repair without R. Natural loop entries
are observations. --control control.json subtracts their count in the treatment's request window, excluding
the initial entry on each arm. Raw times remain in the receipt; count agreement alone does not establish causation.
Run full probe arms last on a stack: StopAllAnimation kills unrelated mapped machines. Rejoin warm before any
compare and after a replay no-request control (which can leave the mapping on the old handle).
--via-replay --no-request --invalidate-only is a premise: release only the mapped idle driver at ISacsProxy,
prove oldValid=0, and return before repair. The real stream/walk must repair it.
--repair-after-invalidate additionally executes the shared repair immediately and proves its stream delta,
valid posture machine and gate mark. Scenario premises use this to avoid assuming a later walk will repair it.
--wait-mapped SECONDS (default 30) waits for a warm join's idle acquire history to reach the real borrowed map.
Only the explicit pending=borrowed-idle precondition is waited on; it returns before any kill or run-key storage.
With `--via-replay` every arm check must also hold; when any is missing the script prints
`verdict=ERROR (arm not armed: <check>)` and exits 2, never FAIL (main's ruling: an arm that never reached the gate
is not evidence about the gate). The checks before injecting the R: `oldValid=0` (the kill worked),
`idleRepairStreamDelta=1` (B2 ran), `postureValid=1` (the posture holds a new, valid machine), `gateMark=1`
(RepairEntryGate has a mark on that handle); after the R: `reresolvedDelta=1` (the kProxyInvalid re-resolve fired) and
`newMapped=1` (the borrowed server driver now maps onto the new machine). The pre-check reports which mapping it found
(`oldHandle` and `oldMapped`); a run that ends on `newMapped=0` is reported as such, no restore step.
--request-delay MS sends the request from a third probe over the bridge (each bridge step costs about 1.2 s, so it
cannot land under that). --ingame-delay-ms MS keeps ONE make probe for both steps: it schedules a per-frame simulator
task that sends the request on the first frame at or after the entry (or, with --via-replay, at or after the repair)
plus MS (0 to 1 frame late; not an AlarmManager alarm, which runs on sim time). The check reports the in-game
requestMs and frames; the script prints the measured entry-to-request gap (requestMs - enterMs) and `delay=OFF` when
it is more than --delay-early-ms (10) before MS or --delay-late-ms (45) after it (the task can only be late). The game
must be unpaused: a new task starts only on a frame with a tick, so while paused the request never goes
(`request pending`, exit 4).
Exit 0 PASS, 4 FAIL, 5 PASS but the in-game delay was off (the arm did not test MS), 2 error (a probe or arm that did
not run, or --via-replay whose arm was not armed). Changes the game: it stops the Sim's idle drivers and re-creates
the idle (run it on a client's copy).
Tests: python -m unittest scripts.idle_repair_probe_test
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import probe_source  # noqa: E402
import sacs_names  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
ACT = ROOT / ".omo" / "run" / "act"
CREATION_STATE = "standing"  # the state the engine's own creation enters (SacsInitStateMachineClient)
RESTART_MS = 1000
LABEL = "repair:probe/stream"
# --ingame-delay-ms: how far the measured entry-to-request gap may be from MS. The in-game task fires only late, 0 to 1
# frame (33 ms at 30 fps) plus the clock's granularity, so the window is asymmetric; 50/150/300 arms stay distinct.
DELAY_EARLY_MS = 10
DELAY_LATE_MS = 45
# --ingame-delay-ms at or under this is the 1-frame arm: the task's first frame is the next frame (about 33 ms at 30 fps),
# so no ms window can hold it. Its rule is instead the NEXT tick (requestTicks - enterTicks == 1).
ONE_FRAME_MAX_MS = 16


MAKE_TEMPLATE = """using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.SACS;

namespace SimsOnline.Scratch
{
    // Generated by scripts/idle_repair_probe.py ( B1 + B2+(a) arms), run __RUN__: kills __SIM__'s standing idle
    // with the same engine call the replay's Z uses on a client (ISacsProxy.StopAllAnimation via SacsRecorder.Current),
    // then, with --via-replay, lets B2 (RepairOwnMachines) acquire/enter/mark the new machine before injecting an R line
    // through ApplyReplayBatch so the replay's kProxyInvalid re-resolve reaches RepairEntryGate's mark. Without
    // --via-replay, re-creates the machine itself and enters __ENTER__, then requests __REQUEST__ with the idle loop's
    // flags (AllowDuplicateRequest|AllowRequestForCurrentState, not yielding). Changes the game.
    public class IdleRepairEntryProbe
    {
        private const string SimName = "__SIM__";
        private const string EnterName = "__ENTER__";
        private const string RequestName = "__REQUEST__";
        private static readonly bool StopWhenAlive = __STOP__;
        private const int InGameDelayMs = __INGAMEDELAY__; // > 0: send the request this long after the entry, in game
        private const int EventCap = 64;
        private const bool ViaReplay = __VIAREPLAY__; // the request goes through the mod's replay (an R line), not the machine
        private const bool InvalidateOnly = __INVALIDATEONLY__; // premise: release only the borrowed idle, leave repair to replay
        private const bool RepairAfterInvalidate = __REPAIRAFTERINVALIDATE__; // premise executor: exercise the shared repair now
        private const string Key = "SimsOnline.IdleRepairProbe.__RUN__";

        private static long Now()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private static string Ticks()
        {
            return Simulator.TicksElapsed().ToString();
        }

        // B2+(a)'s counter on ServerHostMain (a static integer field named like idleRepairDupDropped); -1 when the mod has
        // none, -2 when more than one field matches (no guessing which is the counter).
        private static long DropCount()
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null) { return -1; }
            FieldInfo found = null;
            foreach (FieldInfo f in host.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            {
                if (f.Name.ToLowerInvariant().IndexOf("idlerepairdupdropped", StringComparison.Ordinal) >= 0
                    && (f.FieldType == typeof(int) || f.FieldType == typeof(long)))
                {
                    if (found != null) { return -2; }
                    found = f;
                }
            }
            return found == null ? -1L : Convert.ToInt64(found.GetValue(null));
        }

        // A server driver the replay borrowed onto this client handle, or 0.
        private static uint ServerDriver(uint handle)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null || handle == 0u) { return 0u; }
            BindingFlags s = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo d = host.GetField("sReplayDrivers", s);
            FieldInfo b = host.GetField("sReplayBorrowed", s);
            Dictionary<uint, uint> drivers = d == null ? null : d.GetValue(null) as Dictionary<uint, uint>;
            Dictionary<uint, bool> borrowed = b == null ? null : b.GetValue(null) as Dictionary<uint, bool>;
            if (drivers == null || borrowed == null) { return 0u; }
            foreach (KeyValuePair<uint, uint> e in drivers)
            {
                if (e.Value == handle && borrowed.ContainsKey(e.Key)) { return e.Key; }
            }
            return 0u;
        }

        // The replay's own batch entry with one history R line (seq 0), as the server's idle request would arrive.
        private static string ReplayRequest(uint serverDriver)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            MethodInfo m = host == null ? null : host.GetMethod("ApplyReplayBatch", BindingFlags.NonPublic | BindingFlags.Static,
                null, new Type[] { typeof(string[]) }, null);
            if (m == null) { return "err:no-ApplyReplayBatch"; }
            string context = GetContext();
            SetContext("repair:probe");
            try
            {
                m.Invoke(null, new object[] { new string[] { "0|R|" + serverDriver + "|__ACTORHASH__|__REQUESTHASH__|448" } });
                return "ok";
            }
            catch (Exception ex) { return "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
            finally { SetContext(context); }
        }

        private static Type Host(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(name);
                if (t != null) { return t; }
            }
            return null;
        }

        private static uint Handle(StateMachineClient sm)
        {
            if (sm == null) { return 0u; }
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo driver = typeof(StateMachineClient).GetField("mDriver", all);
            object wrapper = driver == null ? null : driver.GetValue(sm);
            FieldInfo handle = wrapper == null ? null : wrapper.GetType().GetField("mHandle", all);
            return handle == null ? 0u : (uint)handle.GetValue(wrapper);
        }

        // Server drivers the replay borrowed onto this client handle (ServerHostMain.sReplayDrivers, sReplayBorrowed).
        private static int Mapped(uint handle)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null || handle == 0u) { return -1; }
            BindingFlags s = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo d = host.GetField("sReplayDrivers", s);
            FieldInfo b = host.GetField("sReplayBorrowed", s);
            Dictionary<uint, uint> drivers = d == null ? null : d.GetValue(null) as Dictionary<uint, uint>;
            Dictionary<uint, bool> borrowed = b == null ? null : b.GetValue(null) as Dictionary<uint, bool>;
            if (drivers == null || borrowed == null) { return -1; }
            int n = 0;
            foreach (KeyValuePair<uint, uint> e in drivers)
            {
                if (e.Value == handle && borrowed.ContainsKey(e.Key)) { n++; }
            }
            return n;
        }

        private static Sim Find(string first)
        {
            foreach (Sim s in LotManager.Actors)
            {
                if (s != null && s.SimDescription != null && s.FirstName == first) { return s; }
            }
            return null;
        }

        private static string Clean(string text)
        {
            if (text == null) { return "-"; }
            string c = text.Replace(';', ',').Replace('=', ':').Replace('\\n', ' ').Replace('\\r', ' ');
            return c.Length > 80 ? c.Substring(0, 80) : c;
        }

        // --ingame-delay-ms: a Task-type simulator object, which the script host runs once a frame after the first frame
        // with a tick registers it (it never starts while paused; internal documentation), sends the request on the first frame at or after the entry plus
        // InGameDelayMs on the probe's own clock: 0 to 1 frame late. Not AlarmManager: its alarms run on sim time
        // (game speed, pause) and fire from Lot.Simulate only on frames that advance ticks.
        private sealed class DelayedRequest : Sims3.SimIFace.Task
        {
            private readonly StateMachineClient mMachine;
            private readonly long mT0;
            private readonly long mDueMs;
            private readonly string[] mResult; // request, requestMs, frames, requestTicks: read by the check probe (mscorlib only)
            private readonly uint mServerDriver;
            private int mFrames;

            internal DelayedRequest(StateMachineClient machine, long t0, long dueMs, string[] result, uint serverDriver)
            {
                mServerDriver = serverDriver;
                mMachine = machine;
                mT0 = t0;
                mDueMs = dueMs;
                mResult = result;
            }

            public override void Simulate()
            {
                bool done = true;
                try
                {
                    lock (mResult)
                    {
                        if (mResult[0] != "pending") { return; } // the check ended the run first
                        mFrames++;
                        long now = Now() - mT0;
                        if (now < mDueMs) { done = false; return; }
                        string context = GetContext();
                        SetContext("__LABEL__");
                        mResult[1] = now.ToString();
                        mResult[2] = mFrames.ToString();
                        mResult[3] = Ticks();
                        try
                        {
                            if (ViaReplay) { mResult[0] = ReplayRequest(mServerDriver); }
                            else { mMachine.RequestState(false, "x", RequestName, (DriverRequestFlags)384); mResult[0] = "ok"; }
                        }
                        catch (SacsErrorException ex) { mResult[0] = "sacs-error:" + Clean(ex.Message); }
                        catch (Exception ex) { mResult[0] = "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
                        finally { SetContext(context); }
                    }
                }
                catch (Exception ex)
                {
                    lock (mResult) { mResult[0] = "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
                }
                finally
                {
                    if (done)
                    {
                        try { Simulator.DestroyObject(ObjectId); } catch (Exception) { }
                    }
                }
            }
        }

        private static void SetContext(string context)
        {
            Type rec = Host("SimsOnlineServerHost.SacsRecorder");
            FieldInfo f = rec == null ? null : rec.GetField("LocalContext", BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null) { f.SetValue(null, context); }
        }

        private static string GetContext()
        {
            Type rec = Host("SimsOnlineServerHost.SacsRecorder");
            FieldInfo f = rec == null ? null : rec.GetField("LocalContext", BindingFlags.NonPublic | BindingFlags.Static);
            return f == null ? null : f.GetValue(null) as string;
        }

        // Today's repair for the rest (the route bridge, the facial idle) and, with B2, the entry into the server's state.
        private static string RepairOwn(Sim sim)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            MethodInfo m = host == null ? null : host.GetMethod("RepairOwnMachines", BindingFlags.NonPublic | BindingFlags.Static,
                null, new Type[] { typeof(ObjectGuid) }, null);
            if (m == null) { return "missing"; }
            string context = GetContext();
            SetContext("repair:probe");
            try { m.Invoke(null, new object[] { sim.ObjectId }); }
            finally { SetContext(context); }
            return "ok";
        }

        // A ServerHostMain static long counter (sIdleRepairStream, sReplayReresolved), or -1 when the mod lacks the field
        // (or the shape does not fit: only int and long counters are read, so a mismatched name is not silently zeroed).
        private static long Counter(string fieldName)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null) { return -1; }
            FieldInfo f = host.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (f == null) { return -1; }
            if (f.FieldType != typeof(int) && f.FieldType != typeof(long)) { return -1; }
            try { return Convert.ToInt64(f.GetValue(null)); }
            catch (Exception) { return -1; }
        }

        // The same engine call a replayed Z uses on a client (ServerHostMain.cs:10948 in the Z handler:
        // `proxy.StopAllAnimation(target)`, where proxy = SacsRecorder.Current). Sims3.SimIFace.ISacsProxy's
        // StopAllAnimation reaches ScriptCore.SACS.cs:327 -> SACS_StopAllAnimationImpl (native) which invalidates the
        // copy's SACS driver handles; Sims3.SimIFace.Animation's static StopAllAnimation (IAnimation, clip-level) does
        // not, which is why run 1 read oldValid=1 and the gate was never consulted.
        private static string KillIdleDrivers(Sim sim)
        {
            Type rec = Host("SimsOnlineServerHost.SacsRecorder");
            if (rec == null) { return "no-recorder-type"; }
            PropertyInfo cur = rec.GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static);
            if (cur == null) { return "no-current-property"; }
            ISacsProxy proxy = cur.GetValue(null, null) as ISacsProxy;
            if (proxy == null) { return "no-proxy"; }
            try
            {
                if (InvalidateOnly) { proxy.ReleaseDriver(Handle(sim.Standing.CurrentStateMachine)); }
                else { proxy.StopAllAnimation(sim.ObjectId); }
            }
            catch (Exception ex) { return "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
            return "ok";
        }

        // Whether RepairEntryGate (ServerHostMain.sRepairEntries) holds a mark for this client driver handle. Reflection
        // path: sRepairEntries -> its private mMarks Dictionary<uint, Entry> -> ContainsKey(handle). -1 when the shape
        // does not fit (the probe then reports the arm as not armed rather than assuming no mark).
        private static int HasRepairMark(uint handle)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null || handle == 0u) { return -1; }
            FieldInfo sf = host.GetField("sRepairEntries", BindingFlags.NonPublic | BindingFlags.Static);
            if (sf == null) { return -1; }
            object gate;
            try { gate = sf.GetValue(null); } catch (Exception) { return -1; }
            if (gate == null) { return -1; }
            FieldInfo mf = gate.GetType().GetField("mMarks", BindingFlags.NonPublic | BindingFlags.Instance);
            if (mf == null) { return -1; }
            object marks;
            try { marks = mf.GetValue(gate); } catch (Exception) { return -1; }
            if (marks == null) { return -1; }
            MethodInfo ck = marks.GetType().GetMethod("ContainsKey", new Type[] { typeof(uint) });
            if (ck == null) { return -1; }
            try { return (bool)ck.Invoke(marks, new object[] { handle }) ? 1 : 0; }
            catch (Exception) { return -1; }
        }

        public static string Run()
        {
            string context = null;
            bool contextSet = false;
            StateMachineClient subscribed = null; // the machine our StateEntered handler is on, until the run owns it
            SacsEventHandler handler = null;
            bool stored = false;
            try
            {
                if (AppDomain.CurrentDomain.GetData(Key) != null) { return "err=already-ran;run=__RUN__"; }
                Sim sim = Find(SimName);
                if (sim == null) { return "err=no-sim;sim=" + SimName; }
                Sim.StandingPosture standing = sim.Standing as Sim.StandingPosture;
                if (standing == null) { return "err=no-standing-posture;sim=" + SimName; }
                if (sim.Posture != sim.Standing)
                {
                    return "err=not-standing;posture=" + (sim.Posture == null ? "null" : sim.Posture.GetType().Name);
                }

                StateMachineClient old = standing.CurrentStateMachine;
                uint oldHandle = Handle(old);
                bool oldValid = old != null && old.IsValid;
                int oldMapped = Mapped(oldHandle);
                uint viaDriver = 0u;
                long dropBefore = -1;
                long streamBefore = -1;
                if (ViaReplay)
                {
                    dropBefore = DropCount();
                    if (dropBefore == -2) { return "err=pre-check refusal: more than one idleRepairDupDropped field on ServerHostMain"; }
                    if (dropBefore < 0) { return "err=pre-check refusal: no idleRepairDupDropped counter: mod without B2+(a)"; }
                    viaDriver = ServerDriver(oldHandle);
                    if (viaDriver == 0u) { return "err=pre-check refusal: no server idle driver mapped onto the Sim's idle machine;pending=borrowed-idle;oldHandle=" + oldHandle + ";oldMapped=" + oldMapped; }
                    streamBefore = Counter("sIdleRepairStream");
                    if (streamBefore < 0) { return "err=pre-check refusal: no sIdleRepairStream counter: mod without B2"; }
                }
                bool stopped = false;
                string killStatus = "-";
                if (oldValid)
                {
                    if (!StopWhenAlive) { return "err=alive;oldHandle=" + oldHandle + ";oldMapped=" + oldMapped; }
                    // The same engine call a replayed Z uses on the client (see KillIdleDrivers): kills the drivers so
                    // the R line hits kProxyInvalid and the re-resolve takes it to the new machine the gate has marked.
                    killStatus = KillIdleDrivers(sim);
                    stopped = true;
                    oldValid = old != null && old.IsValid;
                }

                if (InvalidateOnly)
                {
                    long repairDelta = -1;
                    int repairedValid = 0;
                    int repairedMark = -1;
                    if (RepairAfterInvalidate && !oldValid && stopped && killStatus == "ok")
                    {
                        // A later route need not run an eligible repair after its posture transitions. Exercise the
                        // real choke point while the proven borrowed standing-idle premise still holds.
                        RepairOwn(sim);
                        repairDelta = Counter("sIdleRepairStream") - streamBefore;
                        StateMachineClient repaired = standing.CurrentStateMachine;
                        repairedValid = repaired != null && repaired.IsValid ? 1 : 0;
                        repairedMark = repaired == null ? -1 : HasRepairMark(Handle(repaired));
                    }
                    return "ok=" + (!oldValid && stopped && killStatus == "ok" ? "true" : "false")
                        + ";oldValid=" + (oldValid ? 1 : 0) + ";oldMapped=" + oldMapped
                        + ";oldHandle=" + oldHandle + ";viaDriver=" + viaDriver + ";kill=" + killStatus
                        + ";idleRepairStreamDelta=" + repairDelta + ";postureValid=" + repairedValid
                        + ";gateMark=" + repairedMark;
                }

                long t0 = Now();
                List<string> entered = new List<string>();
                StateMachineClient sm;
                long streamAfter = streamBefore;
                long streamDelta = -1;
                int postureValid = 0;
                int gateMark = -1;
                string entry = "ok";
                string repair = "missing";

                if (ViaReplay)
                {
                    // The probe makes no machine of its own. Call the repair (RepairOwnMachines under repair:probe), let
                    // B2 acquire the new idle machine, set actor x, put it on the posture, run SetDefaultIdleAnim /
                    // SetIdleCallbacks, enter the stream's state, and mark it in RepairEntryGate.
                    repair = RepairOwn(sim);
                    streamAfter = Counter("sIdleRepairStream");
                    streamDelta = (streamBefore >= 0 && streamAfter >= 0) ? (streamAfter - streamBefore) : -1;
                    sm = standing.CurrentStateMachine;
                    postureValid = (sm != null && sm.IsValid) ? 1 : 0;
                    gateMark = sm == null ? -1 : HasRepairMark(Handle(sm));
                    // The repair's own entry is confirmed by streamDelta == 1; entry=ok only when B2 made one.
                    entry = (streamDelta == 1) ? "ok" : "stream-delta-" + streamDelta;
                    if (sm != null)
                    {
                        // Subscribe AFTER the repair. The engine's StateEntered for B2's entry fires in the next pump
                        // (2-4 ms after EnterState per the plan), so this catches it as the first entry of the state in
                        // the window; a same-pump R that restarts the state adds a second (the FAIL restart shape).
                        SacsEventHandler onEntered = delegate(StateMachineClient sender, IEvent evt)
                        {
                            lock (entered)
                            {
                                if (entered.Count < EventCap) { entered.Add((Now() - t0) + ":" + evt.EventId); }
                            }
                        };
                        sm.AddSacsEventHandler(3u, 0u, onEntered, false); // 3: StateEntered, 0: any state
                        subscribed = sm;
                        handler = onEntered;
                    }
                }
                else
                {
                    // The pre-B1 shape: the probe re-creates the machine itself, without the engine's creation entry.
                    context = GetContext();
                    SetContext("__LABEL__");
                    contextSet = true;
                    sm = StateMachineClient.Acquire(sim.ObjectId, "idle", (AnimationPriority)(-2), false);
                    if (sm == null) { return "err=acquire-null;stopped=" + (stopped ? 1 : 0); }
                    sm.SetActor("x", sim);
                    standing.CurrentStateMachine = sm; // before anything else runs: a posture with no machine stops all animation
                    standing.SetDefaultIdleAnim();
                    standing.SetIdleCallbacks();
                    SacsEventHandler onEntered = delegate(StateMachineClient sender, IEvent evt)
                    {
                        lock (entered)
                        {
                            if (entered.Count < EventCap) { entered.Add((Now() - t0) + ":" + evt.EventId); }
                        }
                    };
                    sm.AddSacsEventHandler(3u, 0u, onEntered, false); // 3: StateEntered, 0: any state
                    subscribed = sm;
                    handler = onEntered;
                    entry = "ok";
                    try { sm.EnterState("x", EnterName); }
                    catch (Exception ex) { entry = "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
                    postureValid = (sm != null && sm.IsValid) ? 1 : 0;
                }

                long enterMs = Now() - t0;
                string enterTicks = Ticks();
                long requestMs = -1;
                string requestTicks = "-1";
                string request = "-";
                string[] delayed = new string[] { "-", "-1", "0", "-1" };
                long reresolvedBefore = ViaReplay ? Counter("sReplayReresolved") : -1;
                if (ViaReplay && RequestName.Length > 0)
                {
                    if (InGameDelayMs > 0)
                    {
                        delayed[0] = "pending";
                        request = "scheduled";
                        Simulator.AddObject(new DelayedRequest(sm, t0, enterMs + InGameDelayMs, delayed, viaDriver));
                    }
                    else
                    {
                        requestMs = Now() - t0;
                        requestTicks = Ticks();
                        request = ReplayRequest(viaDriver);
                    }
                }
                else if (RequestName.Length > 0 && InGameDelayMs > 0)
                {
                    delayed[0] = "pending";
                    request = "scheduled";
                    Simulator.AddObject(new DelayedRequest(sm, t0, enterMs + InGameDelayMs, delayed, viaDriver));
                }
                else if (RequestName.Length > 0)
                {
                    requestMs = Now() - t0;
                    requestTicks = Ticks();
                    try { sm.RequestState(false, "x", RequestName, (DriverRequestFlags)384); request = "ok"; }
                    catch (SacsErrorException ex) { request = "sacs-error:" + Clean(ex.Message); }
                    catch (Exception ex) { request = "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
                }

                // Today's repair for the rest (the route bridge, the facial idle); the posture's machine is alive now.
                // (--via-replay already ran it before the request, so this branch does not re-enter it.)
                if (repair == "missing") { repair = RepairOwn(sim); }

                // The pre-R arm checks the script turns into `verdict=ERROR (arm not armed: <check>)` exit 2.
                int armed = 1;
                string armMissing = "";
                if (ViaReplay)
                {
                    if (stopped && killStatus != "ok") { armed = 0; armMissing = "kill:" + killStatus; }
                    else if (oldValid) { armed = 0; armMissing = "oldValid"; }
                    else if (streamDelta != 1) { armed = 0; armMissing = "idleRepairStream"; }
                    else if (postureValid != 1) { armed = 0; armMissing = "postureValid"; }
                    else if (gateMark != 1) { armed = 0; armMissing = "gateMark"; }
                }

                uint newHandle = Handle(sm);
                AppDomain.CurrentDomain.SetData(Key, new object[] { entered, sm, handler, newHandle, t0, sim.ObjectId.Value, delayed });
                stored = true;
                return "ok=true;run=__RUN__;sim=" + SimName + ";stopped=" + (stopped ? 1 : 0) + ";kill=" + killStatus
                    + ";oldHandle=" + oldHandle + ";oldValid=" + (oldValid ? 1 : 0) + ";oldMapped=" + oldMapped
                    + ";newHandle=" + newHandle
                    + ";idleRepairStreamBefore=" + streamBefore + ";idleRepairStreamAfter=" + streamAfter
                    + ";idleRepairStreamDelta=" + streamDelta
                    + ";postureValid=" + postureValid + ";gateMark=" + gateMark
                    + ";armed=" + armed + ";armMissing=" + (armMissing.Length == 0 ? "-" : armMissing)
                    + ";entry=" + entry + ";enterMs=" + enterMs + ";request=" + request + ";requestMs=" + requestMs
                    + ";enterTicks=" + enterTicks + ";requestTicks=" + requestTicks + ";viaReplay=" + (ViaReplay ? 1 : 0)
                    + ";viaDriver=" + viaDriver + ";dropBefore=" + dropBefore + ";reresolvedBefore=" + reresolvedBefore
                    + ";ingameDelayMs=" + InGameDelayMs + ";repair=" + repair
                    + ";onPosture=" + (standing.CurrentStateMachine == sm ? 1 : 0);
            }
            catch (Exception ex)
            {
                if (subscribed != null && !stored)
                {
                    try { subscribed.RemoveEventHandler(handler); } catch (Exception) { } // no handler left on the game
                }
                return "err=" + ex.GetType().Name + ";msg=" + Clean(ex.Message);
            }
            finally
            {
                if (contextSet) { SetContext(context); }
            }
        }

        // (Run ends; the check probe reads the run array with the same layout as before: entered, sm, handler, newHandle,
        // t0, simId, delayed. `handler` is null when sm was null (--via-replay's arm did not fire); the check guards.)
    }
}
"""

CHECK_TEMPLATE = """using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.SACS;

namespace SimsOnline.Scratch
{
    // Generated by scripts/idle_repair_probe.py ( B1), run __RUN__: what the re-created idle machine did since.
    // Read-only, except that it ends the run (unsubscribes and forgets it). With --request-delay the same source is
    // generated a second time as the request step (FinishRun false): it sends RequestName and leaves the run going.
    public class IdleRepairCheckProbe
    {
        private const string Key = "SimsOnline.IdleRepairProbe.__RUN__";
        private const string RequestName = "__CHECKREQUEST__";
        private const bool FinishRun = __FINISH__;
        private const int DelayMs = __DELAY__; // the --request-delay this run was generated for
        private const int WindowMs = __WINDOW__; // the --window this run was generated for

        private static long Now()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private static string Ticks()
        {
            return Simulator.TicksElapsed().ToString();
        }

        // B2+(a)'s counter on ServerHostMain (a static integer field named like idleRepairDupDropped); -1 when the mod has
        // none, -2 when more than one field matches (no guessing which is the counter).
        private static long DropCount()
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null) { return -1; }
            FieldInfo found = null;
            foreach (FieldInfo f in host.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            {
                if (f.Name.ToLowerInvariant().IndexOf("idlerepairdupdropped", StringComparison.Ordinal) >= 0
                    && (f.FieldType == typeof(int) || f.FieldType == typeof(long)))
                {
                    if (found != null) { return -2; }
                    found = f;
                }
            }
            return found == null ? -1L : Convert.ToInt64(found.GetValue(null));
        }

        private static Type Host(string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(name);
                if (t != null) { return t; }
            }
            return null;
        }

        private static uint Handle(StateMachineClient sm)
        {
            if (sm == null) { return 0u; }
            BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo driver = typeof(StateMachineClient).GetField("mDriver", all);
            object wrapper = driver == null ? null : driver.GetValue(sm);
            FieldInfo handle = wrapper == null ? null : wrapper.GetType().GetField("mHandle", all);
            return handle == null ? 0u : (uint)handle.GetValue(wrapper);
        }

        // Server drivers the replay borrowed onto this client handle (ServerHostMain.sReplayDrivers, sReplayBorrowed).
        private static int Mapped(uint handle)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null || handle == 0u) { return -1; }
            BindingFlags s = BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo d = host.GetField("sReplayDrivers", s);
            FieldInfo b = host.GetField("sReplayBorrowed", s);
            Dictionary<uint, uint> drivers = d == null ? null : d.GetValue(null) as Dictionary<uint, uint>;
            Dictionary<uint, bool> borrowed = b == null ? null : b.GetValue(null) as Dictionary<uint, bool>;
            if (drivers == null || borrowed == null) { return -1; }
            int n = 0;
            foreach (KeyValuePair<uint, uint> e in drivers)
            {
                if (e.Value == handle && borrowed.ContainsKey(e.Key)) { n++; }
            }
            return n;
        }

        private static Sim Find(string first)
        {
            foreach (Sim s in LotManager.Actors)
            {
                if (s != null && s.SimDescription != null && s.FirstName == first) { return s; }
            }
            return null;
        }

        private static string Clean(string text)
        {
            if (text == null) { return "-"; }
            string c = text.Replace(';', ',').Replace('=', ':').Replace('\\n', ' ').Replace('\\r', ' ');
            return c.Length > 80 ? c.Substring(0, 80) : c;
        }

        // A ServerHostMain static long counter (sReplayReresolved, sIdleRepairStream): the same shape the make probe
        // uses, so the check's reresolvedAfter minus the make's reresolvedBefore is the kProxyInvalid re-resolve the
        // arm is looking for. -1 when the field is missing or the shape does not fit.
        private static long Counter(string fieldName)
        {
            Type host = Host("SimsOnlineServerHost.ServerHostMain");
            if (host == null) { return -1; }
            FieldInfo f = host.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (f == null) { return -1; }
            if (f.FieldType != typeof(int) && f.FieldType != typeof(long)) { return -1; }
            try { return Convert.ToInt64(f.GetValue(null)); }
            catch (Exception) { return -1; }
        }

        public static string Run()
        {
            try
            {
                object[] run = AppDomain.CurrentDomain.GetData(Key) as object[];
                if (run == null) { return "err=no-run;run=__RUN__"; }
                List<string> entered = (List<string>)run[0];
                StateMachineClient sm = (StateMachineClient)run[1];
                uint newHandle = (uint)run[3];
                long t0 = (long)run[4];
                Sim sim = new ObjectGuid((ulong)run[5]).ObjectFromId<Sim>();
                if (!FinishRun)
                {
                    long requestMs = Now() - t0;
                    string request;
                    if (sm == null) { request = "err=no-machine"; }
                    else
                    {
                        try { sm.RequestState(false, "x", RequestName, (DriverRequestFlags)384); request = "ok"; }
                        catch (SacsErrorException ex) { request = "sacs-error:" + Clean(ex.Message); }
                        catch (Exception ex) { request = "err:" + ex.GetType().Name + ":" + Clean(ex.Message); }
                    }
                    return "ok=true;run=__RUN__;request=" + request + ";requestMs=" + requestMs + ";delayMs=" + DelayMs;
                }
                Sim.StandingPosture standing = sim == null ? null : sim.Standing as Sim.StandingPosture;
                string events;
                lock (entered) { events = string.Join(",", entered.ToArray()); }
                bool valid = sm != null && sm.IsValid;
                if (sm != null && run[2] != null)
                {
                    try { sm.RemoveEventHandler((SacsEventHandler)run[2]); } catch (Exception) { }
                }
                // --ingame-delay-ms: what the make's in-game request did (still pending: it never fired; ends it).
                string delayedText = "";
                string[] delayed = run.Length > 6 ? run[6] as string[] : null;
                if (delayed != null && delayed[0] != "-")
                {
                    lock (delayed)
                    {
                        delayedText = ";request=" + delayed[0] + ";requestMs=" + delayed[1] + ";frames=" + delayed[2];
                        delayedText += ";requestTicks=" + delayed[3];
                        if (delayed[0] == "pending") { delayed[0] = "ended"; }
                    }
                }
                AppDomain.CurrentDomain.SetData(Key, null);
                return "ok=true;run=__RUN__;dropAfter=" + DropCount() + ";elapsedMs=" + (Now() - t0) + ";windowMs=" + WindowMs + ";valid=" + (valid ? 1 : 0) + delayedText
                    + ";onPosture=" + (standing != null && standing.CurrentStateMachine == sm ? 1 : 0)
                    + ";posture=" + (sim == null || sim.Posture == null ? "-" : sim.Posture.GetType().Name)
                    + ";newMapped=" + Mapped(newHandle)
                    + ";reresolvedAfter=" + Counter("sReplayReresolved")
                    + ";entered=" + (events.Length == 0 ? "-" : events);
            }
            catch (Exception ex) { return "err=" + ex.GetType().Name + ";msg=" + Clean(ex.Message); }
        }
    }
}
"""


def state_name(text: str) -> str:
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_ ]{0,63}", text):
        raise argparse.ArgumentTypeError(f"not a SACS state name: {text!r}")
    return text


def sim_name(text: str) -> str:
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_'-]*", text):
        raise argparse.ArgumentTypeError(f"not a Sim first name: {text!r}")
    return text


def render(template: str, sim: str, enter: str, request: str, stop: bool, run: str,
           delay_ms: int = 0, window_ms: int = 1500, step: bool = False, ingame_delay_ms: int = 0,
           via_replay: bool = False, invalidate_only: bool = False, repair_after_invalidate: bool = False) -> str:
    """step: the check template as the request step (sends the request, leaves the run going)."""
    src = template
    for key, value in (("__SIM__", sim), ("__ENTER__", enter), ("__REQUEST__", request),
                       ("__INGAMEDELAY__", str(ingame_delay_ms)),
                       ("__VIAREPLAY__", "true" if via_replay else "false"),
                       ("__INVALIDATEONLY__", "true" if invalidate_only else "false"),
                       ("__REPAIRAFTERINVALIDATE__", "true" if repair_after_invalidate else "false"),
                       ("__ACTORHASH__", str(sacs_names.fnv32("x"))),
                       ("__REQUESTHASH__", str(sacs_names.fnv32(request) if request else 0)),
                       ("__CHECKREQUEST__", request if step else ""), ("__FINISH__", "false" if step else "true"),
                       ("__DELAY__", str(delay_ms)), ("__WINDOW__", str(window_ms)),
                       ("__STOP__", "true" if stop else "false"), ("__RUN__", run), ("__LABEL__", LABEL)):
        src = src.replace(key, value)
    return src


def reply(text: str) -> dict[str, str]:
    """The probe's key=value fields from bridgectl output (the lines after `loaded=`, or the whole text)."""
    lines = text.splitlines()
    i = next((k for k, ln in enumerate(lines) if ln.lstrip().startswith("loaded=")), -1)
    body = ";".join(re.sub(r"\s*=\s*", "=", ln.strip(), count=1) for ln in lines[i + 1:] if ln.strip())
    fields = {}
    for part in body.split(";"):
        key, sep, value = part.partition("=")
        if sep:
            fields[key.strip()] = value.strip()
    return fields


def entered_events(text: str) -> list[tuple[int, int]]:
    """(ms since the re-creation, state hash) of each StateEntered the check reports ("ms:id,ms:id" or "-")."""
    out = []
    for item in text.split(","):
        ms, sep, sid = item.partition(":")
        if sep and ms.lstrip("-").isdigit() and sid.isdigit():
            out.append((int(ms), int(sid)))
    return out


def verdict(make: dict[str, str], check: dict[str, str], enter: str, request: str,
            *, control: dict[str, str] | None = None) -> tuple[bool, list[str]]:
    """(passed, reasons it failed, or the facts when it passed)."""
    reasons = []
    if make.get("ok") != "true":
        return False, ["make: " + (make.get("err") or "no reply")]
    if check.get("ok") != "true":
        return False, ["check: " + (check.get("err") or "no reply")]
    events = entered_events(check.get("entered", "-"))
    enter_id, creation_id = sacs_names.fnv32(enter), sacs_names.fnv32(CREATION_STATE)
    if make.get("entry") != "ok":
        reasons.append("entry " + make.get("entry", "-"))
    if request and make.get("request") != "ok":
        reasons.append("request " + make.get("request", "-"))
    if any(sid == creation_id for _, sid in events):
        reasons.append(f"the creation state {CREATION_STATE} was entered")
    if not any(sid == enter_id for _, sid in events):
        reasons.append(f"{enter} never reported entered (the machine does not animate)")
    facts = []
    if request:
        since = int(make.get("requestMs", "-1"))
        until = since + RESTART_MS
        natural_events = entered_events((control or {}).get("entered", "-"))
        for name in dict.fromkeys((enter, request)):
            sid = sacs_names.fnv32(name)
            times = [ms for ms, state in events if state == sid]
            natural = [ms for ms, state in natural_events if state == sid]
            # The initial entry can report after a same-pump request. Remove that one event before selecting the
            # request window; earlier loop entries cannot contribute to a delayed request's verdict.
            if name == enter:
                times, natural = times[1:], natural[1:]
            times = [ms for ms in times if since <= ms <= until]
            natural = [ms for ms in natural if since <= ms <= until]
            excess = max(0, len(times) - len(natural) - (name != enter))
            facts.append(f"{name} request-window={since}..{until}ms post={len(times)} natural={len(natural)} "
                         f"excess={excess} times={','.join(map(str, times)) or '-'}")
            if excess:
                initial = next(ms for ms, state in events if state == sid)
                counted = ([initial] if name == enter else []) + times
                reasons.append(f"restart: {name} entered {len(counted)} times by {until} ms "
                               f"({', '.join(map(str, counted))})")
    if check.get("valid") != "1":
        reasons.append("the machine is dead")
    if check.get("onPosture") != "1":
        reasons.append("the posture holds another machine")
    if reasons:
        return False, reasons
    return True, [f"newMapped={check.get('newMapped', '-')}", f"entered={check.get('entered', '-')}", *facts]


def pumped(enter_ticks: str, request_ticks: str) -> str:
    """yes when the simulator counter moved between the entry and the request (the engine's pump ran), no when it did
    not (same pump), - when a tick value is missing."""
    if not (enter_ticks.lstrip("-").isdigit() and request_ticks.lstrip("-").isdigit()) or int(request_ticks) < 0:
        return "-"
    return "yes" if int(request_ticks) > int(enter_ticks) else "no"


def drop_delta(make: dict[str, str], check: dict[str, str]) -> int | None:
    """idleRepairDupDropped after minus before, or None when either read is missing."""
    before, after = make.get("dropBefore", ""), check.get("dropAfter", "")
    if not (before.lstrip("-").isdigit() and after.lstrip("-").isdigit()) or int(before) < 0 or int(after) < 0:
        return None
    return int(after) - int(before)


def reresolved_delta(make: dict[str, str], check: dict[str, str]) -> int | None:
    """sReplayReresolved after minus before (the kProxyInvalid re-resolve the R triggered), or None when either
    read is missing."""
    before, after = make.get("reresolvedBefore", ""), check.get("reresolvedAfter", "")
    if not (before.lstrip("-").isdigit() and after.lstrip("-").isdigit()) or int(before) < 0 or int(after) < 0:
        return None
    return int(after) - int(before)


ARM_PRE_R = ("oldValid", "idleRepairStream", "postureValid", "gateMark", "kill")
ARM_POST_R = ("reresolved", "newMapped")


def arm_check_pre(make: dict[str, str]) -> tuple[bool, str]:
    """(--via-replay) The C# make probe names the first failed pre-R check in `armMissing` and sets `armed=0`;
    for any other missing field the Python side names it too. Returns (ok, name_of_missing_check)."""
    if make.get("armed") == "1":
        return True, ""
    missing = make.get("armMissing", "") or "armed"
    return False, missing


def arm_check_post(make: dict[str, str], check: dict[str, str]) -> tuple[bool, str]:
    """(--via-replay) sReplayReresolved went up by 1 and the borrowed server driver now maps onto the new machine.
    The check's `newMapped` field is what the brief's item 3 names; the re-resolve delta is what makes that mapping
    honest (the replay's retry re-added the line onto the new handle)."""
    if check.get("ok") != "true":
        return False, "check"
    delta = reresolved_delta(make, check)
    if delta is None or delta != 1:
        return False, "reresolved"
    if check.get("newMapped") != "1":
        return False, "newMapped"
    return True, ""


def delay_check(make: dict[str, str], check: dict[str, str], want_ms: int, early_ms: int,
                late_ms: int) -> tuple[bool, str]:
    """(within tolerance, a report line) for --ingame-delay-ms: the gap the game measured from the entry to the request.
    For the 1-frame arm (want_ms <= ONE_FRAME_MAX_MS) the rule is the next tick (+1) from the entry."""
    if want_ms <= ONE_FRAME_MAX_MS:
        et, rt = make.get("enterTicks", "-"), check.get("requestTicks", "-")
        facts = f"1-frame arm: enterTicks {et}, requestTicks {rt}, request {check.get('request', '-')}"
        if pumped(et, rt) != "yes":
            return False, f"delay=OFF: {facts}; the request did not land on a later tick, so the arm did not test the next frame"
        diff = int(rt) - int(et)
        if diff != 1:
            return False, f"delay=OFF: {facts}; landed {diff} ticks after the entry, not the next tick, so the arm did not test the next frame"
        return True, f"delay=ok: {facts}; 1 tick after the entry (the next tick)"
    enter, request = make.get("enterMs", ""), check.get("requestMs", "")
    if not enter.isdigit() or not request.isdigit():
        return False, (f"delay=OFF: no in-game request time (request {check.get('request', '-')}, requestMs {request or '-'}, "
                       f"enterMs {enter or '-'}); the arm did not test {want_ms} ms")
    gap = int(request) - int(enter)
    off = gap - want_ms
    facts = f"want {want_ms} ms, measured {gap} ms (requestMs {request} - enterMs {enter}), {check.get('frames', '-')} frames"
    window = f"tolerance -{early_ms}/+{late_ms} ms"
    if off < -early_ms or off > late_ms:
        return False, f"delay=OFF by {off:+d} ms: {facts}; {window}, so the arm did not test {want_ms} ms"
    return True, f"delay=ok {off:+d} ms: {facts}; {window}"


def bridge(endpoint: str, *args: str, timeout: int = 60) -> str:
    try:
        p = subprocess.run([sys.executable, "scripts/bridgectl.py", "--endpoint", endpoint, "--timeout", str(timeout), *args],
                           cwd=ROOT, capture_output=True, text=True, timeout=timeout + 15)
        return (p.stdout + p.stderr).strip()
    except Exception as ex:  # noqa: BLE001
        return "err=" + type(ex).__name__


def parse_args(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("sim", type=sim_name, help="first name of the standing Sim (a puppet on a client)")
    p.add_argument("--client", default="127.0.0.1:34030", help="the game's admin endpoint (client 1 by default)")
    p.add_argument("--enter", type=state_name, default="standing_idle", help="the state the new machine enters")
    p.add_argument("--request", type=state_name, default="standing_idle", help="the state then requested")
    p.add_argument("--no-request", action="store_true", help="enter only, request nothing")
    p.add_argument("--no-stop", action="store_true", help="refuse when the idle machine is alive instead of stopping it")
    p.add_argument("--window", type=float, default=1.5,
                   help="seconds between the make and the check (>= 1 s restart rule; 10+ for a long --no-request control)")
    p.add_argument("--request-delay", type=int, default=0, metavar="MS",
                   help="send the request MS (0-5000) after the entry, from a step between the make and the check")
    p.add_argument("--ingame-delay-ms", type=int, default=0, metavar="MS",
                   help="send the request MS (0-5000) after the entry from inside the game (one probe, a per-frame "
                        "simulator task; the game must be unpaused), and check the measured gap against the tolerance")
    p.add_argument("--delay-early-ms", type=int, default=DELAY_EARLY_MS, metavar="MS",
                   help=f"how far the measured --ingame-delay-ms gap may fall short of MS (default {DELAY_EARLY_MS})")
    p.add_argument("--delay-late-ms", type=int, default=DELAY_LATE_MS, metavar="MS",
                   help=f"how far the measured --ingame-delay-ms gap may exceed MS (default {DELAY_LATE_MS})")
    p.add_argument("--dry-run", action="store_true", help="write the probes and print them; touch no game")
    p.add_argument("--output", type=Path, help="save this arm's fields and verdict as a JSON receipt")
    p.add_argument("--control", type=Path, help="subtract natural entries from a saved matching --no-request --output receipt")
    p.add_argument("--wait-mapped", type=float, default=30, metavar="SECONDS",
                   help="with --via-replay, wait up to this long for late-join history to borrow the idle before "
                        "starting the arm (0-120, default 30); other refusals remain immediate")
    p.add_argument("--invalidate-only", action="store_true",
                   help="premise: release only the mapped standing idle driver and return before repair; "
                        "requires --via-replay --no-request")
    p.add_argument("--repair-after-invalidate", action="store_true",
                   help="with --invalidate-only, execute the shared repair before returning and require "
                        "idleRepairStream +1, a valid posture machine and its repair gate mark")
    p.add_argument("--via-replay", action="store_true",
                   help="send the request as an R line through the mod's replay batch entry (after RepairOwnMachines, "
                        "under repair:probe) and report the idleRepairDupDropped delta; needs a server idle driver mapped "
                        "onto the Sim and a mod with B2+(a) (exit 2 otherwise). The probe makes no machine of its own: the "
                        "kill (ISacsProxy.StopAllAnimation via SacsRecorder.Current) leaves the posture's idle dead, the "
                        "repair (B2) makes and enters the new one and marks it in RepairEntryGate, and the R hits the old "
                        "dead handle, is re-resolved onto the new one, and reaches TakeRequest. Before the R the script "
                        "requires oldValid=0, idleRepairStreamDelta=1, postureValid=1, gateMark=1; after the R it requires "
                        "reresolvedDelta=1 and newMapped=1. Any missing check exits 2 with "
                        "`verdict=ERROR (arm not armed: <check>)`, never FAIL. With --no-request, record the same "
                        "repair without an R and skip the post-R checks.")
    a = p.parse_args(argv)
    if a.invalidate_only and (not a.via_replay or not a.no_request or a.no_stop):
        p.error("--invalidate-only requires --via-replay --no-request and permits no --no-stop")
    if a.repair_after_invalidate and not a.invalidate_only:
        p.error("--repair-after-invalidate requires --invalidate-only")
    if a.control and (a.no_request or a.invalidate_only):
        p.error("--control is for a request arm")
    if a.window < 1 or not a.window < float("inf"):
        p.error("--window must be finite and at least 1 second")
    if not 0 <= a.wait_mapped <= 120:
        p.error("--wait-mapped must be 0-120 seconds")
    if a.via_replay and a.request_delay:
        p.error("--via-replay sends its R line itself: use --ingame-delay-ms for a delay, not --request-delay")
    if not 0 <= a.request_delay <= 5000:
        p.error("--request-delay must be 0-5000 ms")
    if a.request_delay and a.no_request:
        p.error("--request-delay needs a request (drop --no-request)")
    if not 0 <= a.ingame_delay_ms <= 5000:
        p.error("--ingame-delay-ms must be 0-5000 ms")
    if a.ingame_delay_ms and a.no_request:
        p.error("--ingame-delay-ms needs a request (drop --no-request)")
    if a.ingame_delay_ms and a.request_delay:
        p.error("--ingame-delay-ms and --request-delay are two ways to delay the request: pick one")
    if a.delay_early_ms < 0 or a.delay_late_ms < 0:
        p.error("--delay-early-ms and --delay-late-ms must be 0 or more")
    return a


def main(argv: list[str]) -> int:
    a = parse_args(argv)
    control = None
    control_window = 0
    if a.control:
        try:
            receipt = json.loads(a.control.read_text(encoding="utf-8"))
            if not isinstance(receipt, dict):
                raise ValueError("control receipt must be an object")
            expected = {"sim": a.sim, "client": a.client, "enter": a.enter, "viaReplay": a.via_replay,
                        "request": "", "passed": True}
            if any(receipt.get(k) != v for k, v in expected.items()):
                raise ValueError("control must be a passed no-request arm with the same Sim, client, entry and replay path")
            control_window = receipt["windowMs"]
            if control_window < max(a.request_delay, a.ingame_delay_ms) + RESTART_MS + a.delay_late_ms:
                raise ValueError("control does not cover the request window")
            control = receipt["check"]
            if (not isinstance(control, dict) or not isinstance(control.get("entered"), str)
                    or not entered_events(control["entered"])):
                raise ValueError("control has no StateEntered observations")
        except (OSError, ValueError, KeyError, TypeError) as ex:
            print(f"verdict=ERROR (control: {ex})")
            return 2
    run = uuid.uuid4().hex[:12]
    request = "" if a.no_request else a.request
    delay, window_ms, ingame = a.request_delay, round(a.window * 1000), a.ingame_delay_ms
    extra = {"delay_ms": delay, "window_ms": window_ms}
    # With a delay the make only enters; the request step sends the request. With an in-game delay the make does both.
    make = probe_source.write(ACT / "IdleRepairEntryProbe.cs",
                              render(MAKE_TEMPLATE, a.sim, a.enter, "" if delay else request, not a.no_stop, run,
                                     ingame_delay_ms=ingame, via_replay=a.via_replay,
                                     invalidate_only=a.invalidate_only, repair_after_invalidate=a.repair_after_invalidate,
                                     **extra))
    check = probe_source.write(ACT / "IdleRepairCheckProbe.cs", render(CHECK_TEMPLATE, a.sim, a.enter, request, not a.no_stop, run, **extra))
    make_rel, check_rel = make.relative_to(ROOT).as_posix(), check.relative_to(ROOT).as_posix()
    step_rel = ""
    if delay:
        step_src = render(CHECK_TEMPLATE, a.sim, a.enter, request, not a.no_stop, run, step=True, **extra)
        step_src = step_src.replace("IdleRepairCheckProbe", "IdleRepairRequestProbe")
        step_rel = probe_source.write(ACT / "IdleRepairRequestProbe.cs", step_src).relative_to(ROOT).as_posix()
    if a.dry_run:
        print(f"make probe {make_rel}\ncheck probe {check_rel}\nendpoint {a.client} sim {a.sim} enter {a.enter} "
              f"request {request or '-'}{f' after {delay} ms (step {step_rel})' if delay else ''}"
              f"{f' after {ingame} ms in game (tolerance -{a.delay_early_ms}/+{a.delay_late_ms} ms)' if ingame else ''} "
              f"{' via replay (R line)' if a.via_replay else ''} stop {'no' if a.no_stop else 'when alive'} window {a.window}s")
        print("kill: SacsRecorder.Current.StopAllAnimation (ISacsProxy; the same call the replay's Z handler uses, "
              "ServerHostMain.cs:10948; not the IAnimation static, which leaves oldValid=1)")
        if a.via_replay:
            print("shape: kill -> RepairOwn (B2 acquires/enters/marks) -> subscribe on standing.CurrentStateMachine "
                  "-> ReplayRequest (an R line through ApplyReplayBatch; kProxyInvalid then the re-resolve)")
            print("arm checks before R: oldValid=0; idleRepairStreamDelta=1; postureValid=1; gateMark=1 "
                  "(sRepairEntries.mMarks.ContainsKey on the new handle)")
            print("arm checks after R:  reresolvedDelta=1 (sReplayReresolved+1); newMapped=1 (the borrowed server "
                  "driver maps onto the new machine)")
        else:
            print("arm checks: not applicable (no --via-replay: the probe makes its own machine and the R does not "
                  "go through the replay)")
        return 0
    deadline = time.monotonic() + a.wait_mapped if a.via_replay else 0
    while True:
        made = reply(bridge(a.client, "probe", make_rel))
        # A warm rejoin reports sim_claimed before its presentation history arrives. This refusal occurs before
        # KillIdleDrivers or storing the run key, so it is safe to wait for the real borrow without altering the Sim.
        if not a.via_replay or made.get("pending") != "borrowed-idle":
            break
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        print(f"waiting for borrowed idle mapping: oldHandle={made.get('oldHandle', '-')} "
              f"oldMapped={made.get('oldMapped', '-')} ({remaining:.1f}s left)")
        time.sleep(min(0.25, remaining))
    print("make:  " + ";".join(f"{k}={v}" for k, v in made.items()))
    if made.get("ok") != "true":
        print("verdict=ERROR (the make probe did not run)")
        return 2
    if a.invalidate_only:
        armed = made.get("oldValid") == "0" and int(made.get("oldMapped", "0")) > 0 and made.get("kill") == "ok"
        if a.repair_after_invalidate:
            armed = armed and made.get("idleRepairStreamDelta") == "1" and made.get("postureValid") == "1" and made.get("gateMark") == "1"
        print(("verdict=PASS borrowed idle invalidated; " +
               ("shared stream repair verified" if a.repair_after_invalidate else "repair left to the stream")) if armed else
              "verdict=ERROR (borrowed idle invalidation or requested stream repair did not hold)")
        return 0 if armed else 2
    # The behaviour under test is timed: did the machine re-enter within 1 s of the request. The window is wall time;
    # the game must be unpaused for the machine's events to arrive.
    if delay:
        started = time.monotonic()
        time.sleep(delay / 1000.0)
        stepped = reply(bridge(a.client, "probe", step_rel))
        print("step:  " + ";".join(f"{k}={v}" for k, v in stepped.items()))
        if stepped.get("ok") != "true":
            # The check ends the run (unsubscribes, forgets the key): without it the next run answers already-ran.
            print("check: " + ";".join(f"{k}={v}" for k, v in reply(bridge(a.client, "probe", check_rel)).items()))
            print("verdict=ERROR (the request step did not run)")
            return 2
        made.update(request=stepped.get("request", "-"), requestMs=stepped.get("requestMs", "-1"))
        # The restart rule needs 1 s after the request landed (not after the sleep began: load latency in the step
        # would eat it), and the whole window is still at least --window.
        time.sleep(max(RESTART_MS / 1000.0, a.window - (time.monotonic() - started)))
    else:
        # In game the request lands ingame ms after the entry, which came before the make replied.
        time.sleep(max(a.window, (ingame + RESTART_MS) / 1000.0))
    checked = reply(bridge(a.client, "probe", check_rel))
    print("check: " + ";".join(f"{k}={v}" for k, v in checked.items()))
    timed = True
    if ingame:
        made.update(request=checked.get("request", "-"), requestMs=checked.get("requestMs", "-1"),
                    requestTicks=checked.get("requestTicks", "-1"))
        timed, line = delay_check(made, checked, ingame, a.delay_early_ms, a.delay_late_ms)
        print(line)
    print(f"ticks: enterTicks={made.get('enterTicks', '-')} requestTicks={made.get('requestTicks', '-')} "
          f"pumped={pumped(made.get('enterTicks', '-'), made.get('requestTicks', '-'))}")
    if a.via_replay:
        delta = drop_delta(made, checked)
        if delta is None:
            print("verdict=ERROR (no idleRepairDupDropped reading: mod without B2+(a))")
            return 2
        reres = reresolved_delta(made, checked)
        print(f"idleRepairDupDropped: {delta:+d} (driver {made.get('viaDriver', '-')}) "
              f"sReplayReresolved: {(reres if reres is not None else -999):+d} "
              f"idleRepairStreamDelta={made.get('idleRepairStreamDelta', '-')} "
              f"postureValid={made.get('postureValid', '-')} gateMark={made.get('gateMark', '-')} "
              f"newMapped={checked.get('newMapped', '-')}")
        ok_pre, miss = arm_check_pre(made)
        if not ok_pre:
            print(f"verdict=ERROR (arm not armed: {miss})")
            return 2
        if not a.no_request:
            ok_post, miss = arm_check_post(made, checked)
            if not ok_post:
                print(f"verdict=ERROR (arm not armed: {miss})")
                return 2
    if control is not None and int(made.get("requestMs", "-1")) + RESTART_MS > control_window:
        print("verdict=ERROR (control does not cover the measured request window)")
        return 2
    events = entered_events(checked.get("entered", "-"))
    if events:
        print("entered: " + ", ".join(f"{sacs_names.name32(sid)}@{ms}ms" for ms, sid in events))
    passed, notes = verdict(made, checked, a.enter, request, control=control)
    if a.output:
        a.output.parent.mkdir(parents=True, exist_ok=True)
        a.output.write_text(json.dumps({"sim": a.sim, "client": a.client, "enter": a.enter,
                                       "viaReplay": a.via_replay, "request": request, "windowMs": window_ms,
                                       "make": made, "check": checked, "passed": passed, "timed": timed,
                                       "control": str(a.control) if a.control else None, "notes": notes}, indent=2) + "\n",
                            encoding="utf-8")
    print(("verdict=PASS " if passed else "verdict=FAIL ") + "; ".join(notes))
    return 4 if not passed else 0 if timed else 5


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
