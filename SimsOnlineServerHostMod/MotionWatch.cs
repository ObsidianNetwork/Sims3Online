using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Motion watch (scripts/motion_watch.py): every simulator update, for the objects being watched (Sims), where they are,
    // which way they face, whether a walk is moving them and the animation state this game's recorder last saw on them.
    // The same on a server and a client; the tool pages both out and looks for what a player would see as wrong:
    // teleports, slides (moving without a walk), jitter, and animation states re-entered mid-animation. Wall-clock
    // milliseconds, so the two games' samples line up (one machine, one clock). Bounded; started for a few seconds at a
    // time by SACS_RECORDER watch, read with watch-dump.
    //   ms|object|x|y|z|fx|fz|walking|machine(hex):state|stateSequence
    // and events on the watched objects, so a jump can be traced to what made it:
    //   ms|object|E|what
    // every script placement (IObjects SetPosition / SetForward / SetRotation / SetTransform, through the generated
    // ObjectsRecorder, installed here on a client too) with the mod path it ran under (SacsRecorder.LocalContext: the
    // position mirror, a route snap, the placement replay, a facing settle; "game" for the game's own code, with the
    // nearest managed callers: Callers), slot attaches and detaches (SlotsRecorder), every animation state a watched
    // Sim enters (SacsRecorder: replay, a mod path, or the game with its callers), and the client's walks.
    internal static class MotionWatch
    {
        private const int Capacity = 40000;
        private static readonly List<string> sSamples = new List<string>();
        private static readonly Dictionary<ulong, bool> sWatched = new Dictionary<ulong, bool>();
        private static ulong[] sObjects = new ulong[0];
        private static DateTime sUntil = DateTime.MinValue;
        private static bool sScheduled;
        internal static long Dropped;
        internal static long Updates;

        // The last stream lines this game applied, kept only while a watch runs. When a watched object's position turns
        // invalid (NaN), they and the object's slot parents name what the game had just done: in
        // two-lots-travel-stability (runs 18 and 19, 2026-11-15) Pablo, seated in the moving taxi, read NaN for about
        // 250 ms on both clients at the same place of the ride, with nothing invalid in the lines sent.
        private const int RecentCapacity = 48;
        private static readonly string[] sRecent = new string[RecentCapacity];
        private static int sRecentNext;
        private static readonly Dictionary<ulong, int> sInvalidSince = new Dictionary<ulong, int>();

        internal static void NoteApplied(string line)
        {
            if (line == null || !Active)
            {
                return;
            }

            long ms = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            sRecent[sRecentNext % RecentCapacity] = (ms % 100000L).ToString("00000", CultureInfo.InvariantCulture) + " "
                + (line.Length > 64 ? line.Substring(0, 64) : line);
            sRecentNext++;
        }

        private static bool Invalid(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
        }

        // The applied lines from index first on (oldest first), a few per event so each stays readable.
        private static void NoteRecent(ulong obj, string label, int first)
        {
            first = Math.Max(first, Math.Max(0, sRecentNext - RecentCapacity));
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            int inEvent = 0;
            for (int i = first; i < sRecentNext; i++)
            {
                text.Append(inEvent == 0 ? label + ": " : " ; ").Append(sRecent[i % RecentCapacity]);
                inEvent++;
                if (inEvent == 6 || i == sRecentNext - 1)
                {
                    Note(obj, text.ToString());
                    text.Length = 0;
                    inEvent = 0;
                }
            }
        }

        // A watched object's position turned invalid or valid again: its slot parents (each with its own position) and
        // the lines applied just before, or while it lasted.
        private static void NoteValidity(ObjectGuid id, ulong obj, bool invalid)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (!invalid)
            {
                int since = sInvalidSince[obj];
                sInvalidSince.Remove(obj);
                Note(obj, "position valid again after " + (sRecentNext - since).ToString(c) + " applied line(s)");
                NoteRecent(obj, "applied while invalid", since);
                return;
            }

            sInvalidSince[obj] = sRecentNext;
            string chain = "";
            ObjectGuid at = id;
            for (int depth = 0; depth < 3; depth++)
            {
                ObjectGuid parent = Slots.GetParentId(at);
                if (parent == ObjectGuid.InvalidObjectGuid || !Objects.IsValid(parent))
                {
                    break;
                }

                bool bone = false;
                Slot slot = Slots.GetParentSlot(at, ref bone);
                Vector3 pp = Objects.GetPosition(parent);
                chain += " <- slot " + ((uint)slot).ToString("x8", c) + (bone ? " (bone)" : "") + " of " + parent.Value.ToString(c)
                    + (Invalid(pp) ? " at INVALID" : " at " + pp.x.ToString("F2", c) + "," + pp.y.ToString("F2", c) + "," + pp.z.ToString("F2", c))
                    + (Invalid(Objects.GetForward(parent)) ? " facing INVALID" : "");
                at = parent;
            }

            Note(obj, "position INVALID (NaN)" + (chain.Length == 0 ? "; no slot parent" : ";" + chain));
            NoteRecent(obj, "applied before", sRecentNext - 36);
        }

        internal static bool Active
        {
            get { return DateTime.UtcNow < sUntil; }
        }

        internal static string Start(ulong[] objects, int seconds)
        {
            sSamples.Clear();
            Dropped = 0L;
            Updates = 0L;
            sObjects = objects ?? new ulong[0];
            sWatched.Clear();
            sInvalidSince.Clear();
            sRecentNext = 0;
            for (int i = 0; i < sObjects.Length; i++)
            {
                sWatched[sObjects[i]] = true;
            }

            // Script placements on a client go through the same recorder as on the server (it records nothing there:
            // the ring is off), so a jump has a name.
            if (!ObjectsRecorder.Installed)
            {
                ObjectsRecorder.Install();
            }

            if (!SlotsRecorder.Installed)
            {
                SlotsRecorder.Install();
            }

            // SACS events (state entered, became idle, clip done) on both sides, for the interactions this watch covers:
            // installed here, not at startup, because a StateMachineClient saves its queue (EventQueueRecorder).
            if (!EventQueueRecorder.Installed)
            {
                EventQueueRecorder.Install();
            }

            sUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, Math.Min(seconds, 300)));
            if (!sScheduled)
            {
                sScheduled = true;
                Simulator.AddObject(new WatchTask());
            }

            return "watching=" + sObjects.Length.ToString(CultureInfo.InvariantCulture) + ";seconds=" + seconds.ToString(CultureInfo.InvariantCulture);
        }

        // An event on a watched object (anything that may move it).
        internal static void Note(ulong obj, string what)
        {
            if (!Active || !sWatched.ContainsKey(obj) || sSamples.Count >= Capacity)
            {
                return;
            }

            sSamples.Add((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) + "|"
                + obj.ToString(CultureInfo.InvariantCulture) + "|E|" + (what ?? "-").Replace('|', '/'));
        }

        // An event that is about no watched object itself but may explain one (the transform guard on the car a watched
        // Sim rides in): noted on every watched object.
        internal static void NoteEvery(string what)
        {
            if (!Active || sObjects == null)
            {
                return;
            }

            for (int i = 0; i < sObjects.Length; i++)
            {
                Note(sObjects[i], what);
            }
        }

        // A generated recorder's call (kind|object|Method:codes|args): noted when it is about a watched object.
        internal static void NoteCall(string line)
        {
            try
            {
                int a = line.IndexOf('|');
                int b = a < 0 ? -1 : line.IndexOf('|', a + 1);
                ulong obj;
                if (b < 0 || !ulong.TryParse(line.Substring(a + 1, b - a - 1), NumberStyles.None, CultureInfo.InvariantCulture, out obj) || !sWatched.ContainsKey(obj))
                {
                    return;
                }

                string call = line.Substring(b + 1);
                Note(obj, line.Substring(0, a) + " " + (call.Length > 90 ? call.Substring(0, 90) : call) + " by " + Who());
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Who is acting now: the mod path (SacsRecorder.LocalContext), or the game with its nearest managed callers.
        internal static string Who()
        {
            return SacsRecorder.LocalContext ?? "game via " + Callers(6);
        }

        // The nearest managed frames that are not this mod's recorders or proxies (Type.Method, newest first): which of
        // the game's code made a call the watch notes. Only while a watch runs, only for watched objects.
        internal static string Callers(int max)
        {
            try
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(1, false);
                List<string> names = new List<string>();
                for (int i = 0; i < trace.FrameCount && names.Count < max; i++)
                {
                    MethodBase method = trace.GetFrame(i).GetMethod();
                    Type type = method == null ? null : method.DeclaringType;
                    if (type == null)
                    {
                        continue;
                    }

                    if (type.Namespace == "SimsOnlineServerHost" && (type.Name.IndexOf("Recorder", StringComparison.Ordinal) >= 0
                        || type.Name.IndexOf("Proxy", StringComparison.Ordinal) >= 0 || type.Name == "MotionWatch"))
                    {
                        continue;
                    }

                    names.Add(type.Name + "." + method.Name);
                }

                return names.Count == 0 ? "-" : string.Join("<", names.ToArray());
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static void Sample()
        {
            Updates++;
            CultureInfo c = CultureInfo.InvariantCulture;
            string ms = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(c);
            // The simulator's tick count: a walking Sim moves once a tick, and a frame (one sample) carries 0, 1 or 2
            // ticks, so motion_watch judges a step by the ticks it spans, not the wall time.
            string ticks = Simulator.TicksElapsed().ToString(c);
            for (int i = 0; i < sObjects.Length; i++)
            {
                if (sSamples.Count >= Capacity)
                {
                    Dropped++;
                    continue;
                }

                try
                {
                    ObjectGuid id = new ObjectGuid(sObjects[i]);
                    if (!Objects.IsValid(id))
                    {
                        continue;
                    }

                    Vector3 p = Objects.GetPosition(id);
                    Vector3 f = Objects.GetForward(id);
                    bool invalid = Invalid(p) || Invalid(f);
                    if (invalid != sInvalidSince.ContainsKey(sObjects[i]))
                    {
                        NoteValidity(id, sObjects[i], invalid);
                    }

                    Sim sim = id.ObjectFromId<Sim>();
                    bool walking = sim != null && sim.IsRouting;
                    string state = "-";
                    long sequence = 0L;
                    SacsRecorder.ActorState actor;
                    if (SacsRecorder.Actors.TryGetValue(sObjects[i], out actor))
                    {
                        state = actor.Machine.ToString("x16", c) + ":" + actor.State.ToString(c);
                        sequence = actor.Sequence;
                    }

                    sSamples.Add(ms + "|" + sObjects[i].ToString(c) + "|" + p.x.ToString("F3", c) + "|" + p.y.ToString("F3", c) + "|"
                        + p.z.ToString("F3", c) + "|" + f.x.ToString("F3", c) + "|" + f.z.ToString("F3", c) + "|" + (walking ? "1" : "0") + "|"
                        + state + "|" + sequence.ToString(c) + "|" + ticks);
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }
        }

        // Paged like the recorder's dump: from offset, within maxChars; next = -1 at the end.
        internal static List<string> Page(int offset, int maxChars, out int next)
        {
            List<string> page = new List<string>();
            int chars = 0;
            int i = Math.Max(0, offset);
            for (; i < sSamples.Count; i++)
            {
                if (page.Count > 0 && chars + sSamples[i].Length + 1 > maxChars)
                {
                    break;
                }

                page.Add(sSamples[i]);
                chars += sSamples[i].Length + 1;
            }

            next = i < sSamples.Count ? i : -1;
            return page;
        }

        internal static int Count
        {
            get { return sSamples.Count; }
        }

        // Runs on every simulator update (while the game is paused too) until the watch ends.
        private sealed class WatchTask : Sims3.SimIFace.Task
        {
            public override void Simulate()
            {
                bool keep = Active;
                if (keep)
                {
                    try
                    {
                        Sample();
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }

                if (!keep)
                {
                    sScheduled = false;
                    EventQueueRecorder.Sync();
                    Simulator.DestroyObject(ObjectId);
                }
            }
        }
    }
}
