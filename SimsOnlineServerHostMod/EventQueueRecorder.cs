using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.SimIFace.SACS;

namespace SimsOnlineServerHost
{
    // The SACS event boundary (internal documentation). Every state machine's events reach script through an
    // IEventQueue built from the constructor the engine leaves in AppDomain data ("EventQueueCtor", cached in the internal
    // Sims3.SimIFace.EventQueueHost.sCtorInfo): StateMachineClient.Acquire makes one per driver, and the driver's events
    // (StateEntered, BecameIdle, Interrupted, OneShotStarting/Returning, AnimationDone, the request's Result) arrive in
    // ScriptCore.EventQueue.OnNativeEvent, which calls PermissiveOnEvent first. This subclass of the engine's queue takes
    // PermissiveOnEvent for itself, notes the event (SacsRecorder.RecordEvent: a MotionWatch note while a watch runs), and
    // then calls the script's own handler unchanged, so the game sees every event as before.
    //
    // Why: a request reaches each game at a different point in its clip (2026-10-17, the shower's exit: the copy steps
    // out on its own Loop clip's end, 0-1.8 s after the server's Sim). The request time does not say when a machine
    // actually entered a state; these events do, on both sides.
    //
    // Installed by MotionWatch.Start or while the state catch-up is on (StateCatchUp, tunable sacs.catchup), and
    // uninstalled otherwise (Sync), not at startup: StateMachineClient persists its queue, so a queue of this type would
    // be written into a save. Only the machines acquired meanwhile get one (a save while such a machine is still held
    // needs the mod to load; the dev worlds are not saved after a watch).
    // The client's replay drivers (acquired by the mod, no StateMachineClient) get a queue of their own (Attach), which
    // drops events after noting them.
    internal sealed class EventQueueRecorder : ScriptCore.EventQueue, IEventQueue
    {
        internal static long Created;
        internal static long Events;
        internal static long Attached;

        // The persistent filters a replay driver's queue gets: what a yielding RequestState listens to, without heartbeats, and
        // script events (clip events: an outfit change, a sound, a prop).
        private static readonly uint[] sReplayFilters = new uint[] { 3u, 11u, 10u, 8u, 9u, 5u, 1u, 6u };
        private static readonly Dictionary<uint, EventQueueRecorder> sReplayQueues = new Dictionary<uint, EventQueueRecorder>();

        // The engine's constructor, put back when the watch ends.
        private static ConstructorInfo sOriginal;
        // The native handles of the queues of this type (SacsRecorder marks a driver whose events it sees).
        private static readonly Dictionary<uint, bool> sHandles = new Dictionary<uint, bool>();

        [Persistable(false)]
        private OnEventDelegate mScriptPermissive;

        public EventQueueRecorder()
            : this(false)
        {
        }

        private EventQueueRecorder(bool drain)
        {
            Created++;
            if (sHandles.Count >= 16384)
            {
                sHandles.Clear();
            }

            sHandles[Handle] = true;
            base.PermissiveOnEvent = new OnEventDelegate(OnAny);
            if (drain)
            {
                base.OnEvent = new OnEventDelegate(Drop);
            }
        }

        // StateMachineClient sets this through IEventQueue (EnableSynchronousEventSupport): kept and called after the note.
        OnEventDelegate IEventQueue.PermissiveOnEvent
        {
            set { mScriptPermissive = value; }
        }

        private void OnAny(IEvent evt)
        {
            try
            {
                Events++;
                SacsRecorder.RecordEvent(Handle, evt);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            OnEventDelegate next = mScriptPermissive;
            if (next != null)
            {
                next(evt);
            }
        }

        private static void Drop(IEvent evt)
        {
        }

        internal static bool Owns(uint queueHandle)
        {
            return sHandles.ContainsKey(queueHandle);
        }

        // Installed while a motion watch runs or the state catch-up is on (sacs.catchup: the server streams its machines'
        // StateEntered events, and a client reads its own); the engine's constructor otherwise. Called by MotionWatch at a
        // watch's end and by Tunables when a value changes.
        internal static void Sync()
        {
            if (MotionWatch.Active || StateCatchUp.Mode > 0)
            {
                Install();
            }
            else
            {
                Uninstall();
            }
        }

        private static FieldInfo CtorField()
        {
            Type host = typeof(StateMachineClient).Assembly.GetType("Sims3.SimIFace.EventQueueHost");
            return host == null ? null : host.GetField("sCtorInfo", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                try
                {
                    FieldInfo field = CtorField();
                    ConstructorInfo ctor = field == null ? null : field.GetValue(null) as ConstructorInfo;
                    return ctor != null && ctor.DeclaringType == typeof(EventQueueRecorder);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // bisect: sacs.event-recorder=0 keeps the engine's own queue (no constructor swap, so no queue of this type
        // and no replay queues), to measure the interpreter crash with and without this recorder. The motion watch then
        // has no SACS event notes. Default 1: unchanged. Set it before the process's first watch: queues made while
        // installed outlive an uninstall, so an "off" arm needs a fresh process (Report's ever:0 at its end proves it).
        private static bool sEverInstalled;

        internal static bool Enabled
        {
            get { return Tunables.Get("sacs.event-recorder", 1.0) >= 0.5; }
        }

        internal static string Install()
        {
            try
            {
                if (!Enabled)
                {
                    Uninstall();
                    return "events=off";
                }

                if (Installed)
                {
                    return "events=on";
                }

                ConstructorInfo ctor = typeof(EventQueueRecorder).GetConstructor(Type.EmptyTypes);
                FieldInfo field = CtorField();
                if (ctor == null || field == null)
                {
                    return "events=unavailable";
                }

                ConstructorInfo original = AppDomain.CurrentDomain.GetData("EventQueueCtor") as ConstructorInfo;
                if (original != null && original.DeclaringType != typeof(EventQueueRecorder))
                {
                    sOriginal = original;
                }

                AppDomain.CurrentDomain.SetData("EventQueueCtor", ctor);
                field.SetValue(null, ctor);
                sEverInstalled = true;
                return "events=on";
            }
            catch (Exception ex)
            {
                return "events=error:" + ex.GetType().Name;
            }
        }

        // The engine's own queue back (the watch ended): machines acquired from now on get a ScriptCore.EventQueue.
        internal static void Uninstall()
        {
            try
            {
                if (!Installed)
                {
                    return;
                }

                ConstructorInfo ctor = sOriginal ?? typeof(ScriptCore.EventQueue).GetConstructor(Type.EmptyTypes);
                FieldInfo field = CtorField();
                if (ctor != null && field != null)
                {
                    AppDomain.CurrentDomain.SetData("EventQueueCtor", ctor);
                    field.SetValue(null, ctor);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // A client replay driver (acquired by the mod, so no StateMachineClient and no queue): its own queue, so its events
        // are noted like the server's. Only while installed (a watch started).
        internal static void Attach(ISacsProxy proxy, uint driver)
        {
            try
            {
                if (proxy == null || driver == 0u || !Installed || sReplayQueues.ContainsKey(driver) || sReplayQueues.Count >= 512)
                {
                    return;
                }

                EventQueueRecorder queue = new EventQueueRecorder(true);
                proxy.SetEventQueue(driver, queue.Handle);
                for (int i = 0; i < sReplayFilters.Length; i++)
                {
                    proxy.UpdateEventFilter(driver, 0u, sReplayFilters[i], 0u, EventKeyOperation.kEKOAddPersistent);
                }

                sReplayQueues[driver] = queue;
                Attached++;
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // After the replay driver is released.
        internal static void Detach(uint driver)
        {
            try
            {
                EventQueueRecorder queue;
                if (sReplayQueues.TryGetValue(driver, out queue))
                {
                    sReplayQueues.Remove(driver);
                    queue.Dispose();
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "installed:" + (Installed ? "1" : "0") + ",enabled:" + (Enabled ? "1" : "0") + ",ever:" + (sEverInstalled ? "1" : "0") + ",queues:" + Created.ToString(c) + ",events:" + Events.ToString(c)
                + ",replayQueues:" + sReplayQueues.Count.ToString(c) + "/" + Attached.ToString(c);
        }

        // The same counters as one SACS_RECORDER status field in its name=value form, which scenarios.py arms parses
        // (arms_lib.parse_status keeps only name=value pairs): eventRecorder=on|off (installed), then enabled, ever,
        // queues, events, replayQueues. Gates read e.g. end.server.eventRecorder.ever.
        internal static string StatusField()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "eventRecorder=" + (Installed ? "on" : "off") + ",enabled=" + (Enabled ? "1" : "0") + ",ever=" + (sEverInstalled ? "1" : "0")
                + ",queues=" + Created.ToString(c) + ",events=" + Events.ToString(c)
                + ",replayQueues=" + sReplayQueues.Count.ToString(c) + "/" + Attached.ToString(c);
        }
    }
}
