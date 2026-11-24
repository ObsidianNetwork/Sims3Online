using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.SimIFace.SACS;

namespace SimsOnlineServerHost
{
    // MVP 2/3 (docs/decision-one-simulator.md, /). Every animation the game plays goes
    // through one interface: Sims3.SimIFace.ISacsProxy (StateMachineClient and AnimationUtil read
    // SacsProxy.Instance on every call, and SacsProxy keeps it in one private static field). This
    // decorator is swapped into that field and forwards every call unchanged. It remembers:
    //   - per driver: the state machine (resource instance), its host object, and the object bound to
    //     each actor;
    //   - per object: the last state it was put into (EnterState / RequestState), so the comparison
    //     harness has an animation fact that is the same for interaction replay and presentation replay;
    //   - optionally (ring on), every call as a line, for the presentation replay spike.
    // It never throws into the game: recording failures are swallowed, forwarding is unconditional.
    internal sealed class SacsRecorder : ISacsProxy
    {
        internal sealed class Driver
        {
            public ulong Host;
            public ulong Machine;
            public uint Type;
            public uint Group;
            public int Priority;
            public readonly Dictionary<uint, ulong> Actors = new Dictionary<uint, ulong>();
            // Late join: the driver's own calls (ring lines without seq). Jumping a puppet
            // straight into the current state does not place its body (a sleeping Sim stays standing
            // beside the bed); replaying the path does. Head keeps the first calls (acquire, actors,
            // entry transitions), Tail the latest; Dropped counts calls between them that were lost.
            public readonly List<string> Head = new List<string>();
            public readonly Queue<string> Tail = new Queue<string>();
            public int Dropped;
            // The calls lost between Head and Tail, reduced to what they left behind: the latest actor and parameter
            // settings (by key, in the order last set) and the state each actor last reached. The late-join replay
            // puts these between Head and Tail (settings, then an EnterState per actor), so the Tail's requests start
            // from the state the server's driver was in (push-097: they failed kGenericFailure without it).
            public readonly Dictionary<string, string> GapSettings = new Dictionary<string, string>();
            public readonly List<string> GapSettingOrder = new List<string>();
            public readonly Dictionary<string, string> GapStates = new Dictionary<string, string>();
            public readonly List<string> GapStateOrder = new List<string>();
            // The ring sequence of the driver's latest state call: late join replays drivers oldest first, so the
            // driver the server used last also sets the client's state last.
            public long LastStateSequence;
            // Each actor's latest state on this driver: when the driver whose state an actor shows is released, the
            // actor falls back to its newest state on a driver still alive (see ForgetReleased).
            public readonly Dictionary<ulong, ActorState> LastStates = new Dictionary<ulong, ActorState>();
            // A cosmetic layer each game plays for itself (IsCosmeticMachine): not streamed, not in history, not a
            // Sim's shown state.
            public bool Cosmetic;
            // Who acquired it (AcquireOrigin): the replay, a mod path, the game (on a client with its nearest callers), or
            // adopted from the save. SACS_RECORDER actor prints it as its 7th column.
            public string Origin = "game";
            // Its events reach an EventQueueRecorder (SetEventQueue), and the states it entered since, newest last (UTC
            // ms, at most EnteredCapacity): StateCatchUp reads when a client's machine actually entered a state.
            public bool Observed;
            public readonly List<KeyValuePair<uint, long>> Entered = new List<KeyValuePair<uint, long>>();
            // tier 1 (SacsSeedCopy): the handle did not resolve to a driver at acquire; its AS| line goes out
            // before its first state call instead.
            public bool SeedsPending;
            // tier 1: an AS| line went out for this driver (sacs.seed-copy=2 sends one before the next state call
            // of a driver acquired while the copy was off).
            public bool SeedsSent;
            // tier 1: the last AS| line rung for this driver (SendSeeds, or the copy-on spare-only line). The ring
            // line reaches only clients that take the driver's lines at that moment, so the introductions (DriverHead,
            // SnapshotDriver) carry it right after A|. Cached, not read again: a dump never reads native memory.
            public string SeedLine;
            // tier 1: introduced to a client (DriverHead, SnapshotDriver) while the copy was off, so without
            // SeedLine. The copy-on resend seeds it again even if its seeds went out (DriverIntroduction.CopyOnResend).
            public bool MetWithoutSeeds;
            // tier 1: adopted from the save (Adopt), so no acquire sent its seeds. Its walker counters have run
            // since the load, so, like a driver acquired before the copy turned on, it gets a fresh spare seed sent alone
            // (SacsSeedCopy.SpareLine), before its next state call while the copy is on. Adopt runs on the dump path,
            // which never touches native memory; the state call is the game's own.
            public bool SpareDue;
            // option C (SacsWalkerCopy): introduced to a client since the last bridge tick, which reads its walker
            // counters and rings AW|. Marked on the dump path, read on the tick: a dump never reads native memory.
            public bool WalkersDue;
            // item 4: adopted from the save: how many Head lines Adopt wrote (its A| and S| lines), and the
            // parameter values the driver had when it was first introduced while the copy was on (CaptureAdoptParams),
            // which SnapshotDriver carries right after those lines. Null until then.
            public int AdoptHead;
            public List<string> AdoptParams;
        }

        private const int EnteredCapacity = 8;

        // Facial idles are a cosmetic layer the game's IdleManager plays for the Sims near its own camera, so each
        // process plays them for different Sims at different times (push-097..099: a Sim's latest state was a facial
        // idle on one side and never on the other). Streamed, the server's facial idle calls ran on the client's own
        // facial driver while the client's IdleManager drove it too. Each game keeps its own: the recorder neither
        // streams nor snapshots them, and a Sim's shown state (anim_state) leaves them out on both sides.
        internal static readonly ulong FacialIdleMachine = ResourceUtils.HashString64("facial_idle");


        internal static bool IsCosmeticMachine(ulong machine)
        {
            return machine == FacialIdleMachine;
        }

        private static bool IsCosmeticDriver(uint handle)
        {
            Driver driver;
            return sDrivers.TryGetValue(handle, out driver) && driver.Cosmetic;
        }

        // An object's latest state among live drivers (PROBE_PRESENTATION's sacs field, anim_state). A released
        // driver's state is no longer shown: a client that joins later never had that driver, and the Sim is not
        // animated by it any more (push-098: a Sim whose last state was on a released driver could never match).
        internal sealed class ActorState
        {
            public ulong Machine;
            public uint State;
            public long Sequence;
            public uint Driver;
        }

        public const int RingCapacity = 4096;
        public const int HistoryHead = 64;
        public const int HistoryTail = 64;

        private static readonly Dictionary<uint, Driver> sDrivers = new Dictionary<uint, Driver>();
        internal static readonly Dictionary<ulong, ActorState> Actors = new Dictionary<ulong, ActorState>();
        private static readonly string[] sRing = new string[RingCapacity];
        // game Mono has no Stopwatch.GetTimestamp and cannot resolve Win32
        // QueryPerformanceCounter through DllImport. DateTime.UtcNow was live-probed
        // at ~1 ms resolution. Record it ONLY when the controller opts in to tracing.
        private static readonly long[] sRingUtcTicks = new long[RingCapacity];
        private static readonly long[] sRingUtcSequence = new long[RingCapacity];
        // 1 = directly intercepted ISacsProxy call, 0 = RingLine from another
        // observer (e.g. Slots, position/presentation polling).
        private static readonly bool[] sRingDirectSacs = new bool[RingCapacity];
        internal static bool TraceTimestamps;
        internal static bool RingEnabled;
        internal const long RingCapacityLines = RingCapacity;
        internal static long Sequence;
        internal static long Calls;
        internal static readonly SacsRequestTrace RequestTrace = new SacsRequestTrace();

        // An optional read-only snapshot selector; an older sidecar returns BadIndex (-4).
        private static string RequestSnapshot(uint driver, uint actor)
        {
            if (!SimLinkShadow.WalkersReady)
            {
                return "unavailable:sidecar";
            }
            int read = SimLinkShadow.WalkerRead(driver, -1);
            if ((read & unchecked((int)0xffff0000)) != 0x19500000)
            {
                return "unavailable:" + read.ToString(CultureInfo.InvariantCulture);
            }
            uint error = unchecked((uint)SimLinkShadow.WalkerWord(1000));
            uint synchronized = unchecked((uint)SimLinkShadow.WalkerWord(1001));
            int count = read & 0xffff;
            string group = "";
            int shown = 0;
            for (int i = 0; i < count; i++)
            {
                int start = 1003 + 18 * i;
                if (actor != 0u && unchecked((uint)SimLinkShadow.WalkerWord(start)) != actor)
                {
                    continue;
                }
                uint[] words = new uint[18];
                for (int j = 0; j < words.Length; j++)
                {
                    words[j] = unchecked((uint)SimLinkShadow.WalkerWord(start + j));
                }
                string snapshot = SacsRequestTrace.Snapshot(words, error, synchronized);
                if (actor != 0u)
                {
                    return snapshot;
                }
                group += (shown == 0 ? "" : ";") + "actor:" + U(words[0]) + "{" + snapshot + "}";
                if (++shown == 2)
                {
                    break;
                }
            }
            return actor == 0u ? "actors:" + count.ToString(CultureInfo.InvariantCulture) + ",shown:" + shown.ToString(CultureInfo.InvariantCulture) + "[" + group + "]"
                : "actor-missing;walkers=" + count.ToString(CultureInfo.InvariantCulture);
        }

        // stream gaps: state calls on drivers the recorder never saw acquired (acquired before it
        // was installed). Their lines carry no actor, so a targeted dump drops them and clients never
        // see those transitions. Installing at startup (OnStartupApp) should keep this at 0.
        internal static long UnknownDriverCalls;
        private static readonly Dictionary<uint, bool> sUnknownDrivers = new Dictionary<uint, bool>();
        internal static long AdoptedDrivers;

        // item 4: every driver's latest value per parameter from its start, ring on or off ("0|P|..." lines, keyed
        // as ReplaySetupCache keys them), kept until the driver is released. Kept for drivers the recorder has not adopted
        // yet too, so a driver restored from the save keeps what the game set on it before Adopt. DriverHead carries them.
        private static readonly ReplaySetupCache sParams = new ReplaySetupCache();
        // The parameter hashes seen set on each machine's drivers (MachineParamCapacity each): CaptureParams also reads
        // these on an adopted driver, for parameters the driver's own list does not name.
        private const int MachineParamCapacity = 64;
        private const int MachineCapacity = 256;
        private static readonly Dictionary<ulong, List<uint>> sMachineParams = new Dictionary<ulong, List<uint>>();
        internal static long AdoptParamsRead;
        internal static long AdoptParamsEmpty;
        internal static long AdoptParamsFailed;

        // item 4, on a client: the copy's own SetParam on a driver the replay drives is held (ParamAuthority).
        internal static readonly ParamAuthority Authority = new ParamAuthority();

        // client-local animation: on a client (TrackLocal), every state call made while the mod is
        // not replaying the server's stream is the client's own. Counted per machine ("unknown" for a
        // driver restored from the save). SACS_RECORDER local names the machines by scanning the Sims'
        // state-machine clients (NameMachine). A stack trace taken here hung the client: TS3 runs scripts
        // on its own micro-threads.
        internal static bool TrackLocal;
        internal static bool Replaying;
        internal static long LocalCalls;
        private const int LocalKeyCapacity = 32;
        private static readonly Dictionary<ulong, long> sLocalCounts = new Dictionary<ulong, long>();
        private static readonly Dictionary<ulong, string> sMachineNames = new Dictionary<ulong, string>();

        // Diagnostics (SACS_RECORDER local-contexts): state calls made outside the replay, counted by machine, call, state and
        // the mod code they ran under (LocalContext, set around the mod's own paths that can animate a Sim; "engine" for
        // anything else). A stack trace per call stalled the client's game thread (Mono's StackTrace), so no stacks.
        internal static string LocalContext;
        private static readonly Dictionary<string, long> sLocalContexts = new Dictionary<string, long>();

        internal static List<string> DescribeLocalStacks(int maxChars)
        {
            List<KeyValuePair<string, long>> rows = new List<KeyValuePair<string, long>>(sLocalContexts);
            rows.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            List<string> lines = new List<string>();
            int chars = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                string line = rows[i].Key + "|" + rows[i].Value.ToString(CultureInfo.InvariantCulture);
                if (lines.Count > 0 && chars + line.Length > maxChars)
                {
                    break;
                }

                lines.Add(line);
                chars += line.Length + 1;
            }

            return lines;
        }

        private static void RecordLocalStack(ulong machine, string kind, uint stateHash, ulong actor)
        {
            try
            {
                string key = machine.ToString("x16", CultureInfo.InvariantCulture) + "|" + kind + ":" + stateHash.ToString("x8", CultureInfo.InvariantCulture)
                    + "|" + actor.ToString(CultureInfo.InvariantCulture) + "|" + (LocalContext ?? "engine");
                long seen;
                if (sLocalContexts.TryGetValue(key, out seen))
                {
                    sLocalContexts[key] = seen + 1;
                }
                else if (sLocalContexts.Count < 256)
                {
                    sLocalContexts[key] = 1;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static void RecordLocal(ulong machine)
        {
            LocalCalls++;
            long seen;
            if (sLocalCounts.TryGetValue(machine, out seen))
            {
                sLocalCounts[machine] = seen + 1;
            }
            else if (sLocalCounts.Count < LocalKeyCapacity)
            {
                sLocalCounts[machine] = 1;
            }
        }

        internal static void NameMachine(ulong machine, string name)
        {
            if (!sMachineNames.ContainsKey(machine))
            {
                sMachineNames[machine] = name;
            }
        }

        // "machine|count|holder:name" rows, most calls first, from offset; within maxChars. next = -1 at the end.
        internal static List<string> DescribeLocal(int offset, int maxChars, out int next)
        {
            List<KeyValuePair<ulong, long>> rows = new List<KeyValuePair<ulong, long>>(sLocalCounts);
            rows.Sort(delegate(KeyValuePair<ulong, long> a, KeyValuePair<ulong, long> b) { return b.Value.CompareTo(a.Value); });
            List<string> lines = new List<string>();
            next = -1;
            int chars = 0;
            for (int i = Math.Max(0, offset); i < rows.Count; i++)
            {
                string name;
                if (!sMachineNames.TryGetValue(rows[i].Key, out name))
                {
                    name = rows[i].Key == 0uL ? "unknown-driver" : "-";
                }

                string line = rows[i].Key.ToString("x16", CultureInfo.InvariantCulture) + "|" + rows[i].Value.ToString(CultureInfo.InvariantCulture) + "|" + name;
                if (lines.Count > 0 && chars + line.Length > maxChars)
                {
                    next = i;
                    break;
                }

                lines.Add(line);
                chars += line.Length + 1;
            }

            return lines;
        }

        // stream gaps: StateMachineClient persists its driver handle, so a driver that existed when
        // the save was written comes back on load without any AcquireDriver call (two thirds of a fresh
        // server's calls went to such drivers). The mod finds the client object that owns the handle and
        // hands over what AcquireDriver / SetActor would have told us: host, machine name, actor names.
        // Machine key and actor hashes are computed the way StateMachineClient.Acquire does. The current
        // state is not known; the driver's later calls stream as usual.
        internal static bool Adopt(uint handle, ulong host, string machineName, Dictionary<uint, ulong> actors)
        {
            try
            {
                if (handle == 0u || machineName == null || sDrivers.ContainsKey(handle))
                {
                    return false;
                }

                Driver driver = new Driver();
                driver.Host = host;
                driver.Machine = ResourceUtils.HashString64(machineName);
                driver.Cosmetic = IsCosmeticMachine(driver.Machine);
                driver.Type = 47570707u;
                driver.Group = 0u;
                driver.Priority = -2;
                driver.Origin = LocalContext != null ? "adopted by " + Clean(LocalContext) : "adopted";
                sDrivers[handle] = driver;
                SacsSeedCopy.NoteAcquired(handle, RingEnabled && !driver.Cosmetic, SacsSeedCopy.On, false);
                SacsSeedCopy.NoteAdopted(handle);
                driver.SpareDue = !driver.Cosmetic;
                Remember(handle, "A|" + U(handle) + "|" + U(host) + "|" + U(driver.Type) + "|" + U(driver.Group) + "|"
                    + U(driver.Machine) + "|" + driver.Priority.ToString(CultureInfo.InvariantCulture));
                foreach (KeyValuePair<uint, ulong> actor in actors)
                {
                    driver.Actors[actor.Key] = actor.Value;
                    Remember(handle, "S|" + U(handle) + "|" + U(actor.Key) + "|" + U(actor.Value));
                }

                if (!driver.Cosmetic)
                {
                    driver.AdoptHead = driver.Head.Count; // its parameter values are read later (CaptureAdoptParams)
                }

                sUnknownDrivers.Remove(handle);
                AdoptedDrivers++;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // item 4: an adopted driver's parameter values, as the game left them. What the recorder saw set on the
        // handle before Adopt is in sParams already; the rest is read from the driver for each hash seen set on a driver of
        // its machine (sMachineParams), through GetParam alone. Read offline in TS3W.exe (2026-10-18, scripts/offline analyzer c):
        //   - GetParam (00766fb0 -> 00802050) returns 0 for a driver not found or a hash its machine does not define, else
        //     the driver's value or, while unset, the definition's default (adding the driver's entry for it, as the
        //     game's own reads do). So a default can be carried; the client's definition has the same default.
        //   - GetParamString (00768600) returns the literal "invalid parameter" for an unknown hash and
        //     "<State Machine Driver Not Found>" for a stale handle: not values, so it is not called here.
        //   - GetParamNameByIndex (00768740) dereferences the HashToString lookup of the parameter's name without a null
        //     check, so a name missing from that table would fault the game; the game's scripts never call it, and
        //     neither does this. GetNumParams is safe but useless without the names.
        // A parameter whose hash no recorded call on the machine has set is left out (counted nowhere); one that reads
        // as 0 is counted empty. It runs through the game's own proxy (it reads no SimLink memory), once per adopted
        // driver, from CaptureAdoptParams. Returns the driver's parameter lines after the capture (sParams), which
        // SnapshotDriver carries in fixed slots.
        private static List<string> CaptureParams(uint handle, ulong machine)
        {
            Dictionary<uint, bool> have = new Dictionary<uint, bool>();
            List<string> known = sParams.Lines(handle);
            for (int i = 0; i < known.Count; i++)
            {
                string[] f = known[i].Split('|');
                uint seen;
                if (f.Length > 3 && uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out seen))
                {
                    have[seen] = true;
                }
            }

            SacsRecorder recorder = Current as SacsRecorder;
            ISacsProxy proxy = recorder != null ? recorder.mInner : Current;
            List<uint> hashes;
            if (proxy == null || !sMachineParams.TryGetValue(machine, out hashes))
            {
                return sParams.Lines(handle);
            }

            for (int i = 0; i < hashes.Count; i++)
            {
                uint hash = hashes[i];
                if (have.ContainsKey(hash))
                {
                    continue;
                }

                have[hash] = true;
                try
                {
                    uint value = proxy.GetParam(handle, hash);
                    if (value != 0u)
                    {
                        NoteParam(handle, "P|" + U(handle) + "|" + U(hash) + "|" + U(value));
                        AdoptParamsRead++;
                    }
                    else
                    {
                        AdoptParamsEmpty++;
                    }
                }
                catch (Exception)
                {
                    AdoptParamsFailed++;
                }
            }

            return sParams.Lines(handle);
        }

        // item 4: an adopted driver's parameter values are read the first time it is introduced to a client while
        // the copy is on (DriverHead; SnapshotDriver's first page), not at Adopt. With the copy off, nothing calls GetParam
        // for it (which adds the driver's entry for a parameter it reads, see CaptureParams); with it on, the values are
        // the ones the driver has when a client first follows it.
        // Later settings were recorded (sParams, the driver's history), and go after these lines.
        private static void CaptureAdoptParams(uint handle, Driver driver)
        {
            if (SacsSeedCopy.On && driver.AdoptHead > 0 && driver.AdoptParams == null)
            {
                driver.AdoptParams = CaptureParams(handle, driver.Machine);
            }
        }

        // item 4: the driver's latest value for this parameter (line without seq), whatever the ring; and, on a
        // driver the recorder knows, the hash as one its machine's drivers set.
        private static void NoteParam(uint driverHandle, string line)
        {
            try
            {
                Driver driver;
                bool known = sDrivers.TryGetValue(driverHandle, out driver);
                if (known && driver.Cosmetic)
                {
                    return;
                }

                string full = "0|" + line;
                string[] f = full.Split('|');
                sParams.Keep(driverHandle, f[1], f, full);
                uint hash;
                if (!known || (f[1] != "P" && f[1] != "Ps") || !uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out hash))
                {
                    return;
                }

                List<uint> seen;
                if (!sMachineParams.TryGetValue(driver.Machine, out seen))
                {
                    if (sMachineParams.Count >= MachineCapacity)
                    {
                        return;
                    }

                    seen = new List<uint>();
                    sMachineParams[driver.Machine] = seen;
                }

                if (seen.Count < MachineParamCapacity && !seen.Contains(hash))
                {
                    seen.Add(hash);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // item 4: the copy's own SetParam on a driver the replay drives, while the copy is on (ParamAuthority).
        private static bool HoldOwn(uint driverHandle, string param)
        {
            try
            {
                return Authority.HoldOwn(driverHandle, param, SacsSeedCopy.On, Replaying);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool IsKnown(uint handle)
        {
            return sDrivers.ContainsKey(handle);
        }

        private readonly ISacsProxy mInner;

        private SacsRecorder(ISacsProxy inner)
        {
            mInner = inner;
        }

        private static FieldInfo ProxyField()
        {
            Type proxy = typeof(ISacsProxy).Assembly.GetType("Sims3.SimIFace.SacsProxy");
            return proxy == null ? null : proxy.GetField("gSacsProxy", BindingFlags.NonPublic | BindingFlags.Static);
        }

        // The proxy every StateMachineClient call goes through right now (the recorder when installed),
        // so replayed calls are recorded too and the comparison sees them like local animation.
        internal static ISacsProxy Current
        {
            get
            {
                FieldInfo field = ProxyField();
                return field == null ? null : field.GetValue(null) as ISacsProxy;
            }
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = ProxyField();
                return field != null && field.GetValue(null) is SacsRecorder;
            }
        }

        internal static string Install()
        {
            FieldInfo field = ProxyField();
            if (field == null)
            {
                return "recorder=unavailable";
            }

            object current = field.GetValue(null);
            if (current is SacsRecorder)
            {
                return "recorder=on";
            }

            ISacsProxy inner = current as ISacsProxy;
            if (inner == null)
            {
                return "recorder=no-proxy";
            }

            field.SetValue(null, new SacsRecorder(inner));
            return "recorder=on";
        }

        internal static string Uninstall()
        {
            FieldInfo field = ProxyField();
            SacsRecorder current = field == null ? null : field.GetValue(null) as SacsRecorder;
            if (current != null)
            {
                field.SetValue(null, current.mInner);
            }

            return "recorder=off";
        }

        internal static string Describe()
        {
            return "recorder=" + (Installed ? "on" : "off") + ";ring=" + (RingEnabled ? "on" : "off")
                + ";calls=" + Calls.ToString(CultureInfo.InvariantCulture) + ";sequence=" + Sequence.ToString(CultureInfo.InvariantCulture)
                + ";drivers=" + sDrivers.Count.ToString(CultureInfo.InvariantCulture) + ";actors=" + Actors.Count.ToString(CultureInfo.InvariantCulture)
                + ";unknownDriverCalls=" + UnknownDriverCalls.ToString(CultureInfo.InvariantCulture)
                + ";" + SacsSeedCopy.Describe() + ";" + SacsWalkerCopy.Describe() + ";" + SacsPicks.Describe() + ";" + SacsOrder.Describe() + ";" + SacsRandom.Describe()
                + ";" + SacsGc.Describe() // the GC rate gate's counters for the gc series' gates
                + ";" + SacsFreeRing.Describe() // the explicit-free ring's address and counters
                + ";" + SacsChunkReserve.Describe() // the chunk reserve's install state
                + ";" + SacsOnScreen.Describe() // the on-screen walk's install state
                + ";" + RustRoadPlace.DescribeStatus()
                + ";" + SacsGcHeap.Describe() // the GC heap lever: install state, heap size, forced collections, commit
                + ";" + EventQueueRecorder.StatusField() // enabled= and ever= for the switch arm
                + ";unknownDrivers=" + sUnknownDrivers.Count.ToString(CultureInfo.InvariantCulture)
                + ";adoptedDrivers=" + AdoptedDrivers.ToString(CultureInfo.InvariantCulture)
                + ";params=" + sParams.Count.ToString(CultureInfo.InvariantCulture) + "/adopt:" + AdoptParamsRead.ToString(CultureInfo.InvariantCulture)
                + "/empty:" + AdoptParamsEmpty.ToString(CultureInfo.InvariantCulture)
                + "/failed:" + AdoptParamsFailed.ToString(CultureInfo.InvariantCulture)
                + "/resent:" + ParamsResent.ToString(CultureInfo.InvariantCulture) + ";" + Authority.Describe()
                + ";localCalls=" + LocalCalls.ToString(CultureInfo.InvariantCulture)
                + ";boundaryState=" + BoundaryState.Count.ToString(CultureInfo.InvariantCulture) + "/remembered:" + BoundaryState.Remembered.ToString(CultureInfo.InvariantCulture)
                + "/swept:" + BoundaryState.Swept.ToString(CultureInfo.InvariantCulture) + "/repeats:" + BoundaryState.Repeats.ToString(CultureInfo.InvariantCulture);
        }

        // Drivers whose lines were already sent to a replaying client (dump with targets).
        private static readonly Dictionary<uint, bool> sForwarded = new Dictionary<uint, bool>();

        private static bool Targets(Driver driver, Dictionary<ulong, bool> targets)
        {
            if (targets.ContainsKey(driver.Host))
            {
                return true;
            }

            foreach (ulong actor in driver.Actors.Values)
            {
                if (targets.ContainsKey(actor))
                {
                    return true;
                }
            }

            return false;
        }

        // Ring lines after sinceSeq ("seq|line"), at most max lines, oldest first. With targets, only the
        // lines of drivers that animate a target object (host or actor; the driver table at dump time
        // decides) and StopAllAnimation of a target. A driver seen for the first time gets a synthetic
        // acquire + actor lines ("0|A|..", "0|S|..") from the table first, so a client can build it even
        // when its acquire line went by before the driver touched a target. lastSeq is the last ring line
        // examined (whether returned or not).
        internal static List<string> RingSince(long sinceSeq, int maxChars, Dictionary<ulong, bool> targets, out long lastSeq)
        {
            List<string> lines = new List<string>();
            List<Vector3> targetSpots = null;
            Dictionary<ulong, bool> nearCache = null;
            int chars = 0;
            lastSeq = sinceSeq;
            long first = Math.Max(sinceSeq + 1L, Sequence - RingCapacity + 1L);
            for (long seq = Math.Max(1L, first); seq <= Sequence; seq++)
            {
                // A reply must fit one bridge frame after hex encoding: stop before it grows past
                // maxChars and let the caller page on (more=1).
                if (chars >= maxChars)
                {
                    break;
                }

                lastSeq = seq;
                string line = sRing[(int)(seq % RingCapacity)];
                if (line == null)
                {
                    continue;
                }

                if (targets == null)
                {
                    lines.Add(line);
                    chars += line.Length + 1;
                    continue;
                }

                string[] f = line.Split('|');
                if (f.Length < 3)
                {
                    continue;
                }

                // A sound at a point in the world (AudioRecorder, Au|0|t|handle|x|y|z|hexName: a puddle, a career's music):
                // no object to scope by, so it goes where the point is within stream.world-radius of a streamed Sim, and
                // its stop and property lines follow the handles that went.
                if (f[1] == "Au" && f.Length > 4 && f[2] == "0")
                {
                    if (PointLineConcerns(f, targets, ref targetSpots))
                    {
                        Emit(lines, line, ref chars, false);
                    }

                    continue;
                }

                // Stream clock ticks belong to the stream itself rather than any object; deliver to every client.
                if (f[1] == "Ck")
                {
                    Emit(lines, line, ref chars, false);
                    continue;
                }

                // Card 54's private plan records name the actor after corr, unlike native Rt.
                // They carry no driver handle and must never enter driver scoping below.
                if ((f[1] == "Rt" && f.Length == 7) || (f[1] == "Vp" && f.Length == 10 && f[2] == "rt"))
                {
                    // Card 54 plans come only from the Rust driver, never recorder history.
                    continue;
                }

                if (f[1] == "Vh" || f[1] == "Vp" || f[1] == "Vs" || f[1] == "Ve")
                {
                    ulong vehicleObject;
                    if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out vehicleObject)
                        && (targets.ContainsKey(vehicleObject) || IsProp(vehicleObject)
                            || (f[1] != "Vh" && VehicleRecorder.Concerns(vehicleObject, targets))))
                    {
                        Emit(lines, line, ref chars, false);
                    }
                    continue;
                }

                // item 4: slot attach / detach (SlotsRecorder) and prop visibility (H). Relevant when the
                // object or its new parent is a target, or the object is a prop a target held.
                if (f[1] == "T" || f[1] == "Tx" || f[1] == "B" || f[1] == "U" || f[1] == "H" || f[1] == "Hd" || f[1] == "Ou" || f[1] == "Qu" || f[1] == "Rt" || f[1] == "Rte" || f[1] == "Pz" || f[1] == "Po" || f[1] == "Pl" || f[1] == "Bf" || f[1] == "Rl" || f[1] == "Cv" || f[1] == "Vx" || f[1] == "An" || f[1] == "Wd" || f[1] == "Ob" || f[1] == "Rs" || f[1] == "Rx" || f[1] == "Rh" || f[1] == "Ru" || f[1] == "Au" || f[1] == "Rg" || f[1] == "Ci" || f[1] == "Iv" || f[1] == "Rf" || f[1] == "Us" || f[1] == "Lk")
                {
                    ulong obj;
                    ulong parent = 0uL;
                    if (!ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out obj))
                    {
                        continue;
                    }

                    // T / Tx / B: the new parent; U: the parent it left. (Hd: f[2] is the Sim.)
                    if (f[1] != "H" && f[1] != "Hd" && f[1] != "Ou" && f[1] != "Qu" && f[1] != "Rt" && f[1] != "Rte" && f[1] != "Pz" && f[1] != "Po" && f[1] != "Pl" && f[1] != "Bf" && f[1] != "Rl" && f[1] != "Cv" && f[1] != "Vx" && f[1] != "An" && f[1] != "Wd" && f[1] != "Ob" && f[1] != "Rs" && f[1] != "Rx" && f[1] != "Rh" && f[1] != "Ru" && f[1] != "Au" && f[1] != "Rg" && f[1] != "Ci" && f[1] != "Iv" && f[1] != "Rf" && f[1] != "Us" && f[1] != "Lk" && f.Length > 3)
                    {
                        ulong.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out parent);
                    }

                    bool toTarget = parent != 0uL && targets.ContainsKey(parent);
                    if (toTarget && !IsProp(obj) && Props.Count < PropCapacity)
                    {
                        Props[obj] = true;
                    }

                    if (toTarget || targets.ContainsKey(obj) || (f[1] == "Cv" && ConversationNamesTarget(f, targets)) || IsProp(obj)
                        || NearTargets(obj, f[1], targets, ref targetSpots, ref nearCache))
                    {
                        FlushLate(obj, lines, ref chars);
                        Emit(lines, line, ref chars, false);
                    }
                    else if (f[1] == "T" || f[1] == "Tx" || f[1] == "B" || f[1] == "U" || f[1] == "H" || f[1] == "An" || f[1] == "Wd" || f[1] == "Ob")
                    {
                        KeepLate(obj, line);
                    }

                    continue;
                }

                // an announced object is gone on the server; clients drop the copy they made.
                if (f[1] == "Od")
                {
                    ulong goneId;
                    if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out goneId))
                    {
                        sNearObjects.Remove(goneId); // its last lines (before this one) have gone out
                    }

                    lines.Add(line);
                    chars += line.Length + 1;
                    continue;
                }

                // A lot-keyed world call (Wl: floor features). Lots are few and every game has them all: always sent.
                if (f[1] == "Wl" || f[1] == "En")
                {
                    lines.Add(line);
                    chars += line.Length + 1;
                    continue;
                }

                if (f[1] == "Z")
                {
                    ulong target;
                    if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out target) && targets.ContainsKey(target))
                    {
                        lines.Add(line);
                        chars += line.Length + 1;
                    }

                    continue;
                }

                uint handle;
                if (!uint.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out handle))
                {
                    continue;
                }

                if (f[1] == "X")
                {
                    if (sForwarded.Remove(handle))
                    {
                        lines.Add(line);
                        chars += line.Length + 1;
                    }

                    continue;
                }

                if (!sForwarded.ContainsKey(handle))
                {
                    Driver driver;
                    if (!sDrivers.TryGetValue(handle, out driver) || !Targets(driver, targets))
                    {
                        continue;
                    }

                    sForwarded[handle] = true;
                    if (f[1] != "A")
                    {
                        foreach (string head in DriverHead(handle, driver, f[1] != "AS"))
                        {
                            Emit(lines, head, ref chars, false);
                        }
                    }
                }

                // Emit accounts for the line AND any synthetic object/driver introductions.
                // Counting the same payload again halves effective page capacity and
                // causes unnecessary SACS_RECORDER round trips under contention.
                Emit(lines, line, ref chars, false);
            }

            return lines;
        }

        // Callers check RingEnabled first, so nothing is formatted while the ring is off.
        // item 4: other recorders (SlotsRecorder, the mod's prop visibility poll) write here so their
        // lines share the animation stream's sequence and order.
        internal static void RingLine(string line)
        {
            if (RingEnabled)
            {
                Ring(line, false);
            }
        }

        // Props of interest: objects ever attached to a streamed (target) Sim, so their later detach,
        // re-attach elsewhere and visibility follow them. Filled while filtering the ring; capped.
        internal static readonly Dictionary<ulong, bool> Props = new Dictionary<ulong, bool>();

        // object identity (docs/engine/object-identity.md): before a streamed line first names a
        // non-Sim object (a driver's host, an actor, a slot line's object or parent), the stream carries
        // "O|id|typeFullName|keyInstance|keyType|keyGroup" so a client that does not have that object under
        // that id (a prop created at runtime, a different save copy) can create its own and alias it.
        // History snapshots always include them (a late joiner never saw the earlier ones). The mod sets
        // DescribeObject: null = no such object now, "" = not an object to describe (a Sim, a lot).
        internal delegate string ObjectDescriber(ulong id);
        internal static ObjectDescriber DescribeObject;
        internal static readonly Dictionary<ulong, bool> AnnouncedObjects = new Dictionary<ulong, bool>();
        // Announced objects with a description (not Sims or lots).
        internal static readonly Dictionary<ulong, bool> DescribedObjects = new Dictionary<ulong, bool>();
        private const int AnnouncedCapacity = 4096;

        private static void AnnounceObject(ulong id, List<string> lines, ref int chars, bool always)
        {
            AnnounceObject(id, lines, ref chars, always, false);
        }

        // markProp: the object is an actor in a streamed Sim's animation; a describable one (not a Sim, a lot) becomes a
        // prop, so its visibility (H) and placement (Pl) follow. A cutting board or the food on a plate is not carryable,
        // so its copy was made like furniture: hidden, and never shown.
        private static void AnnounceObject(ulong id, List<string> lines, ref int chars, bool always, bool markProp)
        {
            if (markProp && id != 0uL && !IsProp(id) && Props.Count < PropCapacity && DescribedObjects.ContainsKey(id))
            {
                Props[id] = true;
            }

            if (id == 0uL || DescribeObject == null || (!always && AnnouncedObjects.ContainsKey(id)))
            {
                return;
            }

            string description;
            try
            {
                description = DescribeObject(id);
            }
            catch (Exception)
            {
                return;
            }

            if (description == null)
            {
                return;
            }

            if (AnnouncedObjects.Count < AnnouncedCapacity)
            {
                AnnouncedObjects[id] = true;
            }

            if (description.Length == 0)
            {
                return;
            }

            if (DescribedObjects.Count < AnnouncedCapacity)
            {
                DescribedObjects[id] = true;
            }

            if (markProp && !IsProp(id) && Props.Count < PropCapacity)
            {
                Props[id] = true;
            }

            string line = "0|O|" + U(id) + "|" + description;
            lines.Add(line);
            chars += line.Length + 1;
        }

        // A Cv line concerns a client when any member is one of its targets, not only the pair's first (lower id): a Sim
        // just out of a rabbit hole is no target yet while its partner is, and an unchanged conversation is not sent again
        // (conv-client-cancel, run 20261031-173532: client 1 never got Iliana-Jennifer).
        private static bool ConversationNamesTarget(string[] f, Dictionary<ulong, bool> targets)
        {
            ConversationState cs;
            return ConversationState.Parse(f, out cs) && cs.ShouldForwardTo(targets);
        }

        // The world near the streamed Sims (stream.world-radius metres): an object's effects, sounds, placements, world
        // calls and object animations reach a client too, not only a streamed Sim's and its props'. A fire on the stove
        // (2026-10-14) sent nothing, nor did the stove's own smoke: no streamed Sim held them. Decided once per object per
        // dump, from where it and the streamed Sims are now.
        private static bool NearTargets(ulong obj, string kind, Dictionary<ulong, bool> targets, ref List<Vector3> targetSpots, ref Dictionary<ulong, bool> nearCache)
        {
            if (kind != "Vx" && kind != "Au" && kind != "Ob" && kind != "Wd" && kind != "An" && kind != "Iv" && kind != "Us")
            {
                return false;
            }

            // Once near, an object's lines keep coming until its Od: a fire's stop and dispose lines are dumped after the
            // fire is gone (nowhere to measure), and the client's flames burned on (fire-near, 2026-10-14).
            if (sNearObjects.ContainsKey(obj))
            {
                return true;
            }

            if (nearCache == null)
            {
                nearCache = new Dictionary<ulong, bool>();
            }

            bool near;
            if (nearCache.TryGetValue(obj, out near))
            {
                return near;
            }

            try
            {
                if (targetSpots == null)
                {
                    targetSpots = new List<Vector3>();
                    foreach (ulong target in targets.Keys)
                    {
                        ObjectGuid guid = new ObjectGuid(target);
                        if (Sims3.SimIFace.Objects.IsValid(guid))
                        {
                            targetSpots.Add(Sims3.SimIFace.Objects.GetPosition(guid));
                        }
                    }
                }

                ObjectGuid id = new ObjectGuid(obj);
                if (Sims3.SimIFace.Objects.IsValid(id))
                {
                    Vector3 at = Sims3.SimIFace.Objects.GetPosition(id);
                    float radius = Tunables.Get("stream.world-radius", 25f);
                    for (int i = 0; i < targetSpots.Count && !near; i++)
                    {
                        near = (targetSpots[i] - at).Length() <= radius;
                    }
                }
            }
            catch (Exception)
            {
                near = false;
            }

            nearCache[obj] = near;
            if (near)
            {
                WorldLines++;
                if (sNearObjects.Count < NearObjectsCapacity)
                {
                    sNearObjects[obj] = true;
                }
            }

            return near;
        }

        // Point sounds sent, by the server's handle: their stop (x) and property (s) lines follow.
        private static readonly Dictionary<string, bool> sPointSoundsSent = new Dictionary<string, bool>();
        internal static long PointSoundsSent;

        private static bool PointLineConcerns(string[] f, Dictionary<ulong, bool> targets, ref List<Vector3> targetSpots)
        {
            if (f[3] != "t")
            {
                // A stop stays marked (bounded by the clear below): every ring reader (the push stream, a SACS_DUMP
                // pull) must see it, not only the first.
                return sPointSoundsSent.ContainsKey(f[4]);
            }

            try
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                if (f.Length < 9)
                {
                    return false;
                }

                Vector3 at = new Vector3(float.Parse(f[5], c), float.Parse(f[6], c), float.Parse(f[7], c));
                if (targetSpots == null)
                {
                    targetSpots = new List<Vector3>();
                    foreach (ulong target in targets.Keys)
                    {
                        ObjectGuid guid = new ObjectGuid(target);
                        if (Sims3.SimIFace.Objects.IsValid(guid))
                        {
                            targetSpots.Add(Sims3.SimIFace.Objects.GetPosition(guid));
                        }
                    }
                }

                float radius = Tunables.Get("stream.world-radius", 25f);
                for (int i = 0; i < targetSpots.Count; i++)
                {
                    if ((targetSpots[i] - at).Length() <= radius)
                    {
                        if (sPointSoundsSent.Count > 4096)
                        {
                            sPointSoundsSent.Clear();
                        }

                        sPointSoundsSent[f[4]] = true;
                        PointSoundsSent++;
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return false;
        }

        internal static long WorldLines;
        private static readonly Dictionary<ulong, bool> sNearObjects = new Dictionary<ulong, bool>();
        private const int NearObjectsCapacity = 4096;

        // The objects a ring line names: A host, S actor, T/Tx/B object and parent, U/H object.
        private static void AnnounceObjectsOf(string line, List<string> lines, ref int chars, bool always)
        {
            if (DescribeObject == null)
            {
                return;
            }

            string[] f = line.Split('|');
            if (f.Length < 3)
            {
                return;
            }

            string kind = f[1];
            ulong id;
            if (kind == "Vh" && f.Length > 3 && ulong.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                AnnounceObject(id, lines, ref chars, always, true);
            }
            else if ((kind == "Vp" || kind == "Vs" || kind == "Ve") && ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                AnnounceObject(id, lines, ref chars, always, true);
            }
            else if (kind == "A" && f.Length > 3 && ulong.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                AnnounceObject(id, lines, ref chars, always);
            }
            else if (kind == "S" && f.Length > 4 && ulong.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out id))
            {
                AnnounceObject(id, lines, ref chars, always, true);
            }
            else if (kind == "Lk" && f.Length > 7)
            {
                // Lk|sim|e|task|priority|filter|target|...: the Sim and what it looks at
                if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }

                if (f[3] == "e" && ulong.TryParse(f[7], NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0uL)
                {
                    AnnounceObject(id, lines, ref chars, always);
                }
            }
            else if ((kind == "An" || kind == "Wd" || kind == "Ob" || kind == "Au") && f.Length > 3)
            {
                if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }
            }
            else if (kind == "Vx" && f.Length > 4)
            {
                // Vx|parent|effect|op|...: the parent, and a target object (g)
                if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }

                if (f[4] == "g" && f.Length > 5 && ulong.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }
            }
            else if (kind == "Pl" && f.Length > 3)
            {
                // Pl|obj|parent|slot|bone: both
                if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }

                if (ulong.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }
            }
            else if (kind == "Qu" && f.Length > 3 && f[3] != "-")
            {
                // entries "id:running:target:hexName" joined by ','
                string[] entries = f[3].Split(',');
                for (int i = 0; i < entries.Length; i++)
                {
                    string[] e = entries[i].Split(':');
                    if (e.Length > 2 && ulong.TryParse(e[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    {
                        AnnounceObject(id, lines, ref chars, always);
                    }
                }
            }
            else if (kind == "Hd")
            {
                for (int i = 3; i < f.Length; i++)
                {
                    if (ulong.TryParse(f[i], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                    {
                        AnnounceObject(id, lines, ref chars, always);
                    }
                }
            }
            else if (kind == "T" || kind == "Tx" || kind == "B" || kind == "U" || kind == "H")
            {
                if (ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }

                if (kind != "H" && f.Length > 3 && ulong.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                {
                    AnnounceObject(id, lines, ref chars, always);
                }
            }
        }

        internal static void DescribeSnapshotObject(ulong id, List<string> lines)
        {
            int chars = 0;
            AnnounceObject(id, lines, ref chars, true);
        }

        private static void Emit(List<string> lines, string line, ref int chars, bool always)
        {
            AnnounceObjectsOf(line, lines, ref chars, always);
            lines.Add(line);
            chars += line.Length + 1;
        }
        private const int PropCapacity = 256;

        // Late relevance: an object's slot, visibility and animation lines from before it was of interest. An object often
        // gets them before it becomes a prop (a cutting board is put on the counter, then used; a skill meter gets its
        // transform controller, then becomes an actor), and dropping them left the copy where it was made, hidden, or
        // without its controller. They wait here, bounded, and go out just before the object's first relevant line.
        private const int LatePerObject = 24;
        private const int LateObjects = 512;
        private static readonly Dictionary<ulong, List<string>> sLate = new Dictionary<ulong, List<string>>();
        private static readonly Queue<ulong> sLateOrder = new Queue<ulong>();
        internal static long LateKept;
        internal static long LateFlushed;

        private static void KeepLate(ulong obj, string line)
        {
            List<string> kept;
            if (!sLate.TryGetValue(obj, out kept))
            {
                while (sLate.Count >= LateObjects && sLateOrder.Count > 0)
                {
                    sLate.Remove(sLateOrder.Dequeue());
                }

                kept = new List<string>();
                sLate[obj] = kept;
                sLateOrder.Enqueue(obj);
            }

            if (kept.Count >= LatePerObject)
            {
                kept.RemoveAt(0);
            }

            kept.Add(line);
            LateKept++;
        }

        private static void FlushLate(ulong obj, List<string> lines, ref int chars)
        {
            List<string> kept;
            if (sLate.Count == 0 || !sLate.TryGetValue(obj, out kept))
            {
                return;
            }

            sLate.Remove(obj);
            for (int i = 0; i < kept.Count; i++)
            {
                Emit(lines, kept[i], ref chars, false);
                LateFlushed++;
            }
        }

        private static bool IsProp(ulong id)
        {
            return Props.ContainsKey(id);
        }

        internal static long SourceUtcTicks(long sequence)
        {
            if (sequence <= 0L || sequence > Sequence || Sequence - sequence >= RingCapacity)
            {
                return 0L;
            }
            int index = (int)(sequence % RingCapacity);
            return sRingUtcSequence[index] == sequence ? sRingUtcTicks[index] : 0L;
        }

        internal static bool IsDirectSacsSequence(long sequence)
        {
            if (sequence <= 0L || sequence > Sequence || Sequence - sequence >= RingCapacity)
            {
                return false;
            }
            int index = (int)(sequence % RingCapacity);
            return sRingUtcSequence[index] == sequence && sRingDirectSacs[index];
        }

        // tier 1: the driver's seeds for the clients (AS|driver|context|spare), now or before its next state call.
        private static void SendSeeds(uint handle, Driver driver)
        {
            string seeds = SacsSeedCopy.ReadLine(handle);
            driver.SeedsPending = seeds == null;
            driver.SeedsSent = driver.SeedsSent || seeds != null;
            SacsSeedCopy.NoteServer(handle, true, true, seeds != null);
            if (seeds != null)
            {
                driver.SeedLine = seeds;
                Ring(seeds, false);
            }
        }

        private static void Ring(string line)
        {
            // Invoked directly by ISacsProxy wrappers; distinguish real
            // SACS calls from RingLine's observers and on-demand polling.
            Ring(line, true);
        }

        private static void Ring(string line, bool directSacs)
        {
            if (ClientBoundaryRoute.IsPlanRecord(line, false) || line.StartsWith("Ne|", StringComparison.Ordinal)) return;
            // The stream's timeline (LineClock.cs): a clock line ahead of the first line of each tick that has lines.
            string stamp = LineClock.ServerStamp();
            if (stamp != null)
            {
                RingStore(stamp, false);
            }

            RingStore(line, directSacs);
        }

        private static void RingStore(string line, bool directSacs)
        {
            Sequence++;
            int index = (int)(Sequence % RingCapacity);
            sRing[index] = Sequence.ToString(CultureInfo.InvariantCulture) + "|" + line;
            sRingUtcSequence[index] = Sequence;
            sRingDirectSacs[index] = directSacs;
            sRingUtcTicks[index] = TraceTimestamps ? DateTime.UtcNow.Ticks : 0L;
            // Experiment C: mirror into the SimLink sidecar's ring (only after the controller's handshake).
            if (SimLinkShadow.Enabled)
            {
                SimLinkShadow.Publish(Sequence, line, directSacs);
            }
        }

        private static string U(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string U(uint value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // The lines that rebuild a driver on a client: acquire, seeds, actors and, while the copy is on, the driver's
        // parameter values ( item 4: its P| lines went by before this client followed it) (seq 0, synthetic).
        // seeds: false when the line that introduces the driver is its AS| line, which follows anyway.
        private static List<string> DriverHead(uint handle, Driver driver, bool seeds)
        {
            CaptureAdoptParams(handle, driver); // an adopted driver's values join sParams here
            driver.MetWithoutSeeds = driver.MetWithoutSeeds || !SacsSeedCopy.On;
            MarkWalkers(handle, driver); // option C: its walker counters follow at the next tick
            List<string> actors = new List<string>();
            foreach (KeyValuePair<uint, ulong> actor in driver.Actors)
            {
                actors.Add("S|" + U(handle) + "|" + U(actor.Key) + "|" + U(actor.Value));
            }

            return DriverIntroduction.Head("A|" + U(handle) + "|" + U(driver.Host) + "|" + U(driver.Type) + "|" + U(driver.Group) + "|"
                    + U(driver.Machine) + "|" + driver.Priority.ToString(CultureInfo.InvariantCulture),
                seeds && SacsSeedCopy.On ? driver.SeedLine : null, // before any draw on the client's new driver
                actors, SacsSeedCopy.On ? sParams.Lines(handle) : null);
        }

        // tier 1 (the user, after the crash chase): when the seed copy turns on, every driver acquired before then
        // that has no seeds sent gets a fresh spare seed, sent alone (SacsSeedCopy.SpareLine). The handles are taken at
        // the switch and sent a few seconds later, so the clients' copy is on too (tune.py sets the server first); a
        // driver released or seeded by then is skipped. A driver whose seeds went out but that a client met while the
        // copy was off (MetWithoutSeeds) is in the list too: with sacs.seed-copy=2 its fresh spare goes out with its
        // seeds as they are now, in one line (DriverIntroduction.CopyOnResend).
        // item 4, the same switch: every live driver's parameter values go out again too
        // (DriverIntroduction.ParameterResend). A client that followed a driver while the copy was off got none in its
        // introduction, and a value the game set before then (Idle's highLOD on the save's Sims, adopted and borrowed:
        // 2026-10-21 arms, 4CDDD69B node 07) is not set again, so the copy kept its own. An adopted driver's values are
        // read first (CaptureAdoptParams, now that the copy is on).
        private static readonly long SpareResendDelayTicks = 5L * TimeSpan.TicksPerSecond;
        private static bool sCopyWasOn;
        private static long sSpareResendDue;
        private static List<uint> sSpareResendHandles;
        private static List<uint> sParamResendHandles;
        internal static long ParamsResent;

        internal static void CopyOnTick()
        {
            if (!RingEnabled || !SacsSeedCopy.On)
            {
                sCopyWasOn = false;
                sSpareResendHandles = null;
                sParamResendHandles = null;
                return;
            }

            if (!sCopyWasOn)
            {
                sCopyWasOn = true;
                sSpareResendHandles = new List<uint>();
                sParamResendHandles = new List<uint>();
                foreach (KeyValuePair<uint, Driver> entry in sDrivers)
                {
                    if (!entry.Value.Cosmetic && DriverIntroduction.CopyOnResend(entry.Value.SeedsSent, entry.Value.MetWithoutSeeds,
                            SacsSeedCopy.Late) != DriverIntroduction.CopyOnNone)
                    {
                        sSpareResendHandles.Add(entry.Key);
                    }

                    if (!entry.Value.Cosmetic)
                    {
                        sParamResendHandles.Add(entry.Key);
                    }
                }

                sSpareResendDue = DateTime.UtcNow.Ticks + SpareResendDelayTicks;
                return;
            }

            if (sSpareResendHandles == null || DateTime.UtcNow.Ticks < sSpareResendDue)
            {
                return;
            }

            ResendParams();
            List<uint> handles = sSpareResendHandles;
            sSpareResendHandles = null;
            for (int i = 0; i < handles.Count; i++)
            {
                Driver driver;
                if (!sDrivers.TryGetValue(handles[i], out driver) || driver.Cosmetic)
                {
                    continue;
                }

                int resend = DriverIntroduction.CopyOnResend(driver.SeedsSent, driver.MetWithoutSeeds, SacsSeedCopy.Late);
                if (resend == DriverIntroduction.CopyOnNone)
                {
                    continue;
                }

                driver.SpareDue = false;
                driver.MetWithoutSeeds = false;
                string line = SacsSeedCopy.SpareLine(handles[i]);
                if (line == null)
                {
                    continue;
                }

                if (resend == DriverIntroduction.CopyOnSeeds)
                {
                    // The context as it is now and the fresh spare, one AS| line: a client seeded at acquire writes the
                    // context its draws should have reached, one that met the driver without seeds gets them.
                    SacsSeedCopy.Reseeded++;
                    SendSeeds(handles[i], driver);
                    continue;
                }

                driver.SeedLine = line;
                Ring(line, false);
            }
        }

        // option C (SacsWalkerCopy): drivers introduced to a client wait here, in order, for the tick to read their
        // walker counters; Driver.WalkersDue keeps each in once.
        private static readonly List<uint> sWalkersDue = new List<uint>();

        private static void MarkWalkers(uint handle, Driver driver)
        {
            if (driver.WalkersDue || !WalkerCopyLine.Due(SacsWalkerCopy.On, driver.Cosmetic, driver.SeedLine))
            {
                return;
            }

            driver.WalkersDue = true;
            sWalkersDue.Add(handle);
            SacsWalkerCopy.Marked++;
        }

        // The bridge tick's AW| lines: at most WalkerCopyLine.PerTick drivers, the oldest marks first; a driver released
        // since its mark is skipped. With the copy off, the marks are dropped.
        internal static void WalkerTick()
        {
            if (sWalkersDue.Count == 0)
            {
                return;
            }

            bool on = RingEnabled && SacsWalkerCopy.On;
            int taken = on ? Math.Min(sWalkersDue.Count, WalkerCopyLine.PerTick) : sWalkersDue.Count;
            for (int i = 0; i < taken; i++)
            {
                Driver driver;
                if (!sDrivers.TryGetValue(sWalkersDue[i], out driver) || !driver.WalkersDue)
                {
                    continue;
                }

                driver.WalkersDue = false;
                string line = on && !driver.Cosmetic ? SacsWalkerCopy.ReadLine(sWalkersDue[i]) : null;
                if (line != null)
                {
                    Ring(line, false);
                }
            }

            sWalkersDue.RemoveRange(0, taken);
        }

        // The copy-on parameter resend (see CopyOnTick): the handles taken at the switch, a driver released by now skipped.
        // A P| line for a driver a client does not map is skipped there as unmapped; a replayed SetParam passes
        // ParamAuthority, so the server's value replaces the copy's own.
        private static void ResendParams()
        {
            List<uint> handles = sParamResendHandles;
            sParamResendHandles = null;
            if (handles == null)
            {
                return;
            }

            for (int i = 0; i < handles.Count; i++)
            {
                Driver driver;
                if (!sDrivers.TryGetValue(handles[i], out driver) || driver.Cosmetic)
                {
                    continue;
                }

                CaptureAdoptParams(handles[i], driver);
                List<string> lines = DriverIntroduction.ParameterResend(sParams.Lines(handles[i]));
                for (int j = 0; j < lines.Count; j++)
                {
                    Ring(lines[j], false);
                    ParamsResent++;
                }
            }
        }

        // Crash chase (sacs.seed-read-loop=N, server, seed copy off): read both seeds of every live recorded driver N
        // times per bridge tick, ringing nothing. Released drivers have left sDrivers, so no stale handle is read.
        internal static void ReadLoopTick()
        {
            if (!RingEnabled || SacsSeedCopy.On)
            {
                return;
            }

            int rounds = SacsSeedCopy.ReadLoop;
            if (rounds <= 0)
            {
                return;
            }

            List<uint> handles = new List<uint>();
            foreach (KeyValuePair<uint, Driver> entry in sDrivers)
            {
                if (!entry.Value.Cosmetic)
                {
                    handles.Add(entry.Key);
                }
            }

            for (int round = 0; round < rounds; round++)
            {
                for (int i = 0; i < handles.Count; i++)
                {
                    SacsSeedCopy.ReadOnly(handles[i]);
                }
            }
        }

        // Late join: the live drivers that animate a target object right now, the one whose latest state call is
        // oldest first (the dictionary's order is arbitrary once drivers come and go).
        internal static List<uint> SnapshotDrivers(Dictionary<ulong, bool> targets)
        {
            EnvironmentRecorder.Capture();
            List<KeyValuePair<long, uint>> found = new List<KeyValuePair<long, uint>>();
            foreach (KeyValuePair<uint, Driver> entry in sDrivers)
            {
                if (!entry.Value.Cosmetic && Targets(entry.Value, targets) && !FinishedForHistory(entry.Key))
                {
                    found.Add(new KeyValuePair<long, uint>(entry.Value.LastStateSequence, entry.Key));
                }
            }

            found.Sort(delegate (KeyValuePair<long, uint> a, KeyValuePair<long, uint> b)
            {
                int bySequence = a.Key.CompareTo(b.Key);
                return bySequence != 0 ? bySequence : a.Value.CompareTo(b.Value);
            });
            List<uint> handles = new List<uint>(found.Count + 1);
            // The engine boundaries' state first (a pseudo driver, BoundaryState): what the drivers' calls play on.
            BoundaryState.LastTargets = targets;
            handles.Add(BoundaryState.Handle);
            for (int i = 0; i < found.Count; i++)
            {
                handles.Add(found[i].Value);
            }

            return handles;
        }

        private static bool FinishedForHistory(uint handle)
        {
            // Native cleanup also requires the absence of external owners. A graph can have no
            // pending work while its posture still owns a visible idle or carrying layer.
            if (SimLinkShadow.NativeVersion < 16 || SimLinkShadow.HasNoPendingWork(handle) != 1)
            {
                return false;
            }
            Driver driver;
            if (!sDrivers.TryGetValue(handle, out driver) || ServerHostMain.PresentationDriverOwned(handle, driver.Host))
            {
                return false;
            }
            foreach (ulong actor in driver.Actors.Values)
            {
                if (ServerHostMain.PresentationDriverOwned(handle, actor))
                {
                    return false;
                }
            }
            return true;
        }

        // How many of the driver's calls fell between Head and Tail (0 when none, or the driver is unknown).
        // The machines animating this actor on this game, each with the actor's latest state on it:
        //   driver|machine (hex)|state hash|sequence|cosmetic 0/1|alive 0/1
        // alive: the engine still has the driver's event queue. A driver whose Sim dropped it (a replaced posture machine)
        // stays listed with its last state, so a dead row is history, not a second machine animating the Sim.
        // On a server, the drivers the stream carries; on a client, the replayed ones and its own (scripts/sim_compare.py
        // --machines puts both side by side).
        internal static List<string> ActorMachines(ulong actor)
        {
            List<string> rows = new List<string>();
            ISacsProxy proxy = Current;
            foreach (KeyValuePair<uint, Driver> entry in sDrivers)
            {
                ActorState state;
                if (!entry.Value.LastStates.TryGetValue(actor, out state))
                {
                    continue;
                }

                bool alive = proxy == null || proxy.IsEventQueueValid(entry.Key);

                rows.Add(entry.Key.ToString(CultureInfo.InvariantCulture) + "|" + entry.Value.Machine.ToString("x16", CultureInfo.InvariantCulture) + "|"
                    + state.State.ToString(CultureInfo.InvariantCulture) + "|" + state.Sequence.ToString(CultureInfo.InvariantCulture) + "|"
                    + (entry.Value.Cosmetic ? "1" : "0") + "|" + (alive ? "1" : "0") + "|" + (entry.Value.Origin ?? "game"));
            }

            return rows;
        }

        // Who acquires a driver now ( tooling 6): the replay (with the mod path it runs under, if any), a mod path
        // (LocalContext: repair, presenter, stand-up, ...), or the game. On a client (the ring off) the game's nearest
        // managed callers too: a copy's own Idle beside the replay's made the crumpled pose of 2026-10-21, and which code
        // acquired it (RepairOwnMachines) had to be inferred. A server acquires for every Sim in town, so no stack there.
        private static string AcquireOrigin()
        {
            if (Replaying)
            {
                return LocalContext != null ? "replay:" + Clean(LocalContext) : "replay";
            }

            if (LocalContext != null)
            {
                return Clean(LocalContext);
            }

            return RingEnabled ? "game" : "game via " + Clean(MotionWatch.Callers(3));
        }

        // A free-text column: no row or field separator, at most 120 characters.
        private static string Clean(string text)
        {
            if (text == null)
            {
                return "-";
            }

            string clean = text.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
            return clean.Length > 120 ? clean.Substring(0, 120) : clean;
        }

        // item 4 live check (SACS_RECORDER param <driver> <name|hash>): one parameter of a driver on this game,
        // read through the game's proxy as a hash (GetParam; yes and no named) and as a string (GetParamString), with the
        // line the recorder kept for it (sParams) and whether the replay follows the driver (ParamAuthority, a client).
        // param: a name ("highLOD", hashed as SetParameter hashes it) or a hash as 8 hex digits. Reads only.
        internal static string ReadParam(uint handle, string param)
        {
            if (handle == 0u || string.IsNullOrEmpty(param))
            {
                return "error=usage: param <driver> <name|hash x8>";
            }

            uint hash;
            if (param.Length != 8 || !uint.TryParse(param, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
            {
                hash = ResourceUtils.HashString32(param);
            }

            SacsRecorder recorder = Current as SacsRecorder;
            ISacsProxy proxy = recorder != null ? recorder.mInner : Current;
            if (proxy == null)
            {
                return "error=no proxy";
            }

            uint value;
            string text;
            try
            {
                value = proxy.GetParam(handle, hash);
                text = proxy.GetParamString(handle, hash);
            }
            catch (Exception e)
            {
                return "error=" + e.GetType().Name;
            }

            string kept = "none";
            List<string> lines = sParams.Lines(handle);
            for (int i = 0; i < lines.Count; i++)
            {
                string[] f = lines[i].Split('|');
                if (f.Length > 4 && (f[1] == "P" || f[1] == "Ps") && f[3] == U(hash))
                {
                    kept = lines[i];
                }
            }

            text = text == null ? string.Empty : text.Replace('\n', ' ').Replace(';', ',');
            return "driver=" + U(handle) + ";param=" + hash.ToString("X8", CultureInfo.InvariantCulture)
                + ";value=" + value.ToString("X8", CultureInfo.InvariantCulture) + (value == ParamYes ? "(yes)" : value == ParamNo ? "(no)" : string.Empty)
                + ";string=" + text + ";kept=" + kept + ";followed=" + (Authority.Follows(handle) ? "1" : "0");
        }

        internal static int SnapshotDropped(uint handle)
        {
            Driver driver;
            return sDrivers.TryGetValue(handle, out driver) ? driver.Dropped : 0;
        }

        // Late join: replay the driver's own calls (Head then Tail, seq 0) so a client plays the path to the
        // current state, transitions included. Paged: lines from offset until maxChars, next = the offset
        // to ask for next or -1 at the end. Marks the driver forwarded, so later ring lines flow via dump.
        internal static List<string> SnapshotDriver(uint handle, int offset, int maxChars, out int next)
        {
            if (handle == BoundaryState.Handle)
            {
                return BoundaryState.Page(offset, maxChars, out next); // the engine boundaries' state (late join)
            }

            List<string> lines = new List<string>();
            next = -1;
            Driver driver;
            if (!sDrivers.TryGetValue(handle, out driver) || (offset <= 0 && FinishedForHistory(handle)))
            {
                return lines;
            }

            sForwarded[handle] = true;
            // tier 1: the seeds right after the acquire (Head[0]), before any draw. The slot is always there
            // (null: none, or the copy off) so a seed sent between two pages cannot shift the offsets; that seed's ring
            // line reaches this client anyway, the driver being forwarded from here on. Item 4: an adopted driver's
            // parameter values, after its A| and S| lines, in slots fixed when they were first read (null while the copy
            // is off). They are read on a first page only, so the pages of one snapshot keep their offsets unless the
            // copy turns on between two of them and DriverHead reads them meanwhile.
            if (offset <= 0)
            {
                CaptureAdoptParams(handle, driver);
            }

            List<string> gapSettings = new List<string>();
            for (int i = 0; i < driver.GapSettingOrder.Count; i++)
            {
                gapSettings.Add(driver.GapSettings[driver.GapSettingOrder[i]]);
            }

            List<string> gapStates = new List<string>();
            for (int i = 0; i < driver.GapStateOrder.Count; i++)
            {
                gapStates.Add(driver.GapStates[driver.GapStateOrder[i]]);
            }

            List<string> adoptParams = null;
            if (driver.AdoptParams != null)
            {
                adoptParams = new List<string>();
                for (int i = 0; i < driver.AdoptParams.Count; i++)
                {
                    adoptParams.Add(driver.AdoptParams[i].Substring(2)); // the cache's lines carry seq 0; the pager adds it
                }
            }

            driver.MetWithoutSeeds = driver.MetWithoutSeeds || !SacsSeedCopy.On;
            List<string> all = DriverIntroduction.Snapshot(driver.Head, SacsSeedCopy.On ? driver.SeedLine : null, driver.AdoptHead,
                adoptParams, SacsSeedCopy.On, gapSettings, gapStates, driver.Tail);
            int chars = 0;
            for (int i = Math.Max(0, offset); i < all.Count; i++)
            {
                if (chars >= maxChars)
                {
                    next = i;
                    break;
                }

                if (all[i] == null)
                {
                    continue;
                }

                string line = "0|" + all[i];
                Emit(lines, line, ref chars, true);
            }

            if (next < 0)
            {
                // Option C: the last page. A joiner's live lines are held until its whole history went out
                // (AuthoritativeSession.PublishPresentationAsync), so the AW| line lands after the Tail's requests.
                MarkWalkers(handle, driver);
            }

            return lines;
        }

        private static void Remember(uint driverHandle, string line)
        {
            if (ClientBoundaryRoute.IsPlanRecord(line, false) || line.StartsWith("Ne|", StringComparison.Ordinal)) return;
            try
            {
                Driver driver;
                if (!sDrivers.TryGetValue(driverHandle, out driver) || driver.Cosmetic)
                {
                    return;
                }

                if (driver.Head.Count < HistoryHead)
                {
                    driver.Head.Add(line);
                    return;
                }

                driver.Tail.Enqueue(line);
                if (driver.Tail.Count > HistoryTail)
                {
                    KeepGap(driver, driver.Tail.Dequeue());
                    driver.Dropped++;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Called synchronously before the higher-epoch page-zero sync ack.
        internal static void DiscardPrivatePlans()
        {
            ClientBoundaryRoute.DiscardPlanRecords(sRing, true);
            foreach (Driver driver in sDrivers.Values)
            {
                ClientBoundaryRoute.DiscardPlanRecords(driver.Head, false);
                string[] tail = driver.Tail.ToArray();
                driver.Tail.Clear();
                foreach (string line in tail)
                    if (!ClientBoundaryRoute.IsPlanRecord(line, false)) driver.Tail.Enqueue(line);
            }
            // Gap settings/states and sParams accept only native SACS kinds; seeds are AS.
            // sLate accepts only object-boundary kinds, and snapshot lists only driver metadata.
        }

        // A call dropped from the middle of a driver's history: keep what it left behind (see Driver.GapSettings).
        //   E|R|driver|actorHash|stateHash|flags|actor  -> the state the actor reached (replayed as EnterState)
        //   S|driver|actorHash|object                   -> the latest actor binding
        //   P|Ps|driver|param|value, Pn|Ph|driver|prefix|name|value -> the latest parameter value
        // Interrupts and other calls leave nothing to restore.
        private static void KeepGap(Driver driver, string dropped)
        {
            string[] f = dropped.Split('|');
            if (f.Length < 3)
            {
                return;
            }

            string kind = f[0];
            if (kind == "R" && f.Length > 9 && f[8] != "0")
            {
                // A posture machine handing the Sim to another (a bridge): kept as the request it was, or the next machine
                // waits on a hand-off that never comes (a late joiner's Sim standing still beside its phone).
                Keep(driver.GapStates, driver.GapStateOrder, f[2], dropped);
            }
            else if ((kind == "E" || kind == "R") && f.Length >= 6)
            {
                Keep(driver.GapStates, driver.GapStateOrder, f[2], "E|" + f[1] + "|" + f[2] + "|" + f[3] + "|0|" + f[5]);
            }
            else if (kind == "S" || kind == "P" || kind == "Ps")
            {
                Keep(driver.GapSettings, driver.GapSettingOrder, kind + "|" + f[2], dropped);
            }
            else if ((kind == "Pn" || kind == "Ph") && f.Length >= 4)
            {
                Keep(driver.GapSettings, driver.GapSettingOrder, kind + "|" + f[2] + "|" + f[3], dropped);
            }
        }

        private static void Keep(Dictionary<string, string> map, List<string> order, string key, string line)
        {
            if (map.ContainsKey(key))
            {
                order.Remove(key);
            }

            map[key] = line;
            order.Add(key);
        }

        private static void RecordState(uint driverHandle, uint actorHash, uint stateHash, string kind, uint flags)
        {
            RecordState(driverHandle, actorHash, stateHash, kind, flags, string.Empty);
        }

        // rest: the call's further arguments, appended after the actor (R: |wakeup|timeout|bridgeDriver|bridgeActor).
        private static void RecordState(uint driverHandle, uint actorHash, uint stateHash, string kind, uint flags, string rest)
        {
            try
            {
                Calls++;
                Driver driver;
                ulong actor = 0uL;
                ulong machine = 0uL;
                if (sDrivers.TryGetValue(driverHandle, out driver))
                {
                    machine = driver.Machine;
                    driver.Actors.TryGetValue(actorHash, out actor);
                    if (driver.SpareDue && RingEnabled && SacsSeedCopy.On && !driver.SeedsSent
                        && (sSpareResendHandles == null || !sSpareResendHandles.Contains(driverHandle)))
                    {
                        // An adopted driver (a pending copy-on resend that covers it sends its spare instead).
                        driver.SpareDue = false;
                        string spare = SacsSeedCopy.SpareLine(driverHandle);
                        if (spare != null)
                        {
                            driver.SeedLine = spare;
                            Ring(spare, false);
                        }
                    }

                    if ((driver.SeedsPending || (!driver.SeedsSent && SacsSeedCopy.Late)) && RingEnabled && SacsSeedCopy.On)
                    {
                        SendSeeds(driverHandle, driver);
                        if (driver.SeedsSent)
                        {
                            // Option C: a late seeding sends the spare the server's walkers have counted under since
                            // acquire; the copy's restart at the AS| line, so their counts follow too.
                            MarkWalkers(driverHandle, driver);
                        }
                    }

                    Remember(driverHandle, kind + "|" + U(driverHandle) + "|" + U(actorHash) + "|" + U(stateHash) + "|" + U(flags) + "|" + U(actor) + rest);
                }
                else
                {
                    UnknownDriverCalls++;
                    if (sUnknownDrivers.Count < 1024)
                    {
                        sUnknownDrivers[driverHandle] = true;
                    }
                }

                bool cosmetic = driver != null && driver.Cosmetic;
                if (RingEnabled && !cosmetic)
                {
                    Ring(kind + "|" + U(driverHandle) + "|" + U(actorHash) + "|" + U(stateHash) + "|" + U(flags) + "|" + U(actor) + rest);
                }
                else if (!cosmetic)
                {
                    Sequence++;
                }

                if (driver != null && !cosmetic)
                {
                    driver.LastStateSequence = Sequence;
                }
                if (TrackLocal && !Replaying)
                {
                    RecordLocal(machine);
                    RecordLocalStack(machine, kind, stateHash, actor);
                }


                if (actor != 0uL && !cosmetic)
                {
                    ActorState state;
                    if (!Actors.TryGetValue(actor, out state))
                    {
                        state = new ActorState();
                        Actors[actor] = state;
                    }

                    state.Machine = machine;
                    state.State = stateHash;
                    state.Sequence = Sequence;
                    state.Driver = driverHandle;
                    if (driver != null)
                    {
                        ActorState own;
                        if (!driver.LastStates.TryGetValue(actor, out own))
                        {
                            own = new ActorState();
                            driver.LastStates[actor] = own;
                        }

                        own.Machine = machine;
                        own.State = stateHash;
                        own.Sequence = Sequence;
                        own.Driver = driverHandle;
                    }

                    if (MotionWatch.Active)
                    {
                        // scripts/motion_watch.py: which state a watched Sim entered and who asked (the stream's replay,
                        // a mod path, the game's own code), so a restarted idle or a clip replayed mid-way has a name. A
                        // repair inside the batch (a standing Po's, RepairLabel.Posture) is the mod's own entry, not the stream's.
                        MotionWatch.Note(actor, "state " + kind + " " + machine.ToString("x16", CultureInfo.InvariantCulture) + ":" + stateHash.ToString(CultureInfo.InvariantCulture)
                            + " by " + (Replaying && !RepairLabel.IsRepair(LocalContext) ? "replay" : MotionWatch.Who()));
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // SACS events by queue (EventQueueRecorder): the driver each queue serves, set in SetEventQueue.
        private static readonly Dictionary<uint, uint> sQueueDrivers = new Dictionary<uint, uint>();
        // A BecameIdle, Interrupted or Heartbeat event carries the actor's hash and is followed at once by one carrying the
        // state's (StateMachineClient's yield loop reads them as a pair): the leader by queue, until its follower arrives.
        private static readonly Dictionary<uint, ulong> sEventLeaders = new Dictionary<uint, ulong>();
        private static readonly string[] sEventNames = new string[]
        {
            "Wildcard", "Result", "Message", "StateEntered", "Heartbeat", "AnimationDone", "ScriptEvent", "OverlayLevel",
            "OneShotStarting", "OneShotReturning", "Interrupted", "BecameIdle"
        };
        internal static long EventNotes;
        // A machine idle in a state reports BecameIdle every tick: noted once per state, until it enters another.
        private static readonly Dictionary<uint, uint> sIdleNoted = new Dictionary<uint, uint>();

        // One SACS event on a queue (EventQueueRecorder, both sides), noted for the motion watch while it runs:
        //   sacs <event> <machine hex>:<state> [extra]
        // StateEntered, OneShotStarting/Returning: the state in the event, for every actor on the driver. BecameIdle and
        // Interrupted: the actor and state from the pair. AnimationDone (the actor's clip ended), Result (the request's
        // code) and ScriptEvent (a clip event's id): the actor's latest state on the driver. Heartbeats are not noted.
        internal static void RecordEvent(uint queueHandle, IEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            uint driverHandle;
            Driver driver;
            if (!sQueueDrivers.TryGetValue(queueHandle, out driverHandle) || !sDrivers.TryGetValue(driverHandle, out driver) || driver.Cosmetic)
            {
                return;
            }

            uint subType = evt.EventSubType;
            uint id = evt.EventId;
            if (subType == 3u)
            {
                sIdleNoted.Remove(driverHandle);
                driver.Entered.Add(new KeyValuePair<uint, long>(id, StateCatchUp.NowMs()));
                if (driver.Entered.Count > EnteredCapacity)
                {
                    driver.Entered.RemoveAt(0);
                }

                // The server's machine entered a state: Se|driver|state for the clients' StateCatchUp (only while it is on).
                if (RingEnabled && StateCatchUp.Mode > 0)
                {
                    Ring("Se|" + U(driverHandle) + "|" + U(id), false);
                }

                Steer(driverHandle, driver, id);
                SteerQueue(driverHandle, driver, id);
            }

            ulong leader;
            if (sEventLeaders.TryGetValue(queueHandle, out leader))
            {
                sEventLeaders.Remove(queueHandle);
                uint leaderType = (uint)(leader >> 32);
                ulong actor;
                uint idle;
                if (leaderType == 11u && sIdleNoted.TryGetValue(driverHandle, out idle) && idle == id)
                {
                    return;
                }

                if (leaderType == 11u && sIdleNoted.Count < 4096)
                {
                    sIdleNoted[driverHandle] = id;
                }

                if (leaderType != 4u && driver.Actors.TryGetValue((uint)leader, out actor))
                {
                    NoteEvent(actor, driver, sEventNames[leaderType], id, string.Empty);
                }

                return;
            }

            if (subType == 10u || subType == 11u || subType == 4u)
            {
                if (sEventLeaders.Count < 4096)
                {
                    sEventLeaders[queueHandle] = ((ulong)subType << 32) | id;
                }

                return;
            }

            if (!MotionWatch.Active || subType == 2u || subType == 7u || subType >= (uint)sEventNames.Length)
            {
                return;
            }

            bool observeClips = subType == 3u && sObserveClips.Value > 0f;
            foreach (KeyValuePair<uint, ulong> entry in driver.Actors)
            {
                if (subType == 5u && entry.Key != id)
                {
                    continue;
                }

                if (subType == 3u || subType == 8u || subType == 9u)
                {
                    NoteEvent(entry.Value, driver, sEventNames[subType], id, string.Empty);
                    if (observeClips)
                    {
                        NoteActiveClips(entry.Value, driver, id);
                    }

                    continue;
                }

                ActorState last;
                uint state = driver.LastStates.TryGetValue(entry.Value, out last) ? last.State : 0u;
                NoteEvent(entry.Value, driver, sEventNames[subType], state, subType == 5u ? string.Empty : " id=" + U(id));
            }
        }

        // StateCatchUp: the driver's events reach a queue of ours; it entered the state at or after ms; an actor's object;
        // the driver's machine.
        internal static bool Observed(uint driverHandle)
        {
            Driver driver;
            return sDrivers.TryGetValue(driverHandle, out driver) && driver.Observed;
        }

        internal static bool EnteredSince(uint driverHandle, uint state, long ms)
        {
            Driver driver;
            if (!sDrivers.TryGetValue(driverHandle, out driver))
            {
                return false;
            }

            for (int i = driver.Entered.Count - 1; i >= 0; i--)
            {
                if (driver.Entered[i].Value < ms)
                {
                    break;
                }

                if (driver.Entered[i].Key == state)
                {
                    return true;
                }
            }

            return false;
        }

        internal static ulong ActorOf(uint driverHandle, uint actorHash)
        {
            Driver driver;
            ulong actor;
            return sDrivers.TryGetValue(driverHandle, out driver) && driver.Actors.TryGetValue(actorHash, out actor) ? actor : 0uL;
        }

        internal static ulong MachineOf(uint driverHandle)
        {
            Driver driver;
            return sDrivers.TryGetValue(driverHandle, out driver) ? driver.Machine : 0uL;
        }

        private static void NoteEvent(ulong actor, Driver driver, string name, uint state, string extra)
        {
            EventNotes++;
            MotionWatch.Note(actor, "sacs " + name + " " + driver.Machine.ToString("x16", CultureInfo.InvariantCulture) + ":" + U(state) + extra);
        }

        // observe: the clips an actor plays as its machine enters a state, for the motion watch:
        //   sacs clips <machine hex>:<state> n=<count> <clip>@<priority>,...
        // At a loop hub's StateEntered it still names the clip that just ended (the previous cycle's variant), so the
        // new pick shows only in a later sample (scripts/loop_variant_table.py maps a clip to its variant). Off unless
        // the tunable sacs.observe-clips is set above 0.
        // The script proxy (ScriptCore.SACS) stubs GetActiveClipCount/GetActiveClipInfo in the release game (count 0, info
        // null), but the natives behind its private externs are real (TS3W.exe 00767930 walks the actor's animation
        // controllers), so this calls those. SACS_GetActiveClipCountImpl hands the list's handle back through a
        // CallbackObject SimCallback (callerId, data, dataType, shouldReset); OnClipHandle keeps the data.
        private static MethodInfo sClipCount;
        private static MethodInfo sClipInfo;
        private static MethodInfo sClipDestroy;
        private static Delegate sClipCallback;
        private static object sClipHandle;
        private static string sClipsUnavailable;

        private static void OnClipHandle(ulong callerId, object data, int dataType, bool shouldReset)
        {
            sClipHandle = data;
        }

        private static bool ClipNatives()
        {
            if (sClipCallback != null)
            {
                return true;
            }

            if (sClipsUnavailable != null)
            {
                return false;
            }

            try
            {
                Type sacs = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    sacs = sacs ?? assembly.GetType("ScriptCore.SACS");
                }

                BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
                Type simCallback = sacs == null || sacs.BaseType == null ? null : sacs.BaseType.GetNestedType("SimCallback", BindingFlags.NonPublic);
                sClipCount = sacs == null ? null : sacs.GetMethod("SACS_GetActiveClipCountImpl", flags);
                sClipInfo = sacs == null ? null : sacs.GetMethod("SACS_GetActiveClipInfoImpl", flags);
                sClipDestroy = sacs == null ? null : sacs.GetMethod("SACS_DestroyClipInfoHandleImpl", flags);
                if (simCallback == null || sClipCount == null || sClipInfo == null || sClipDestroy == null)
                {
                    sClipsUnavailable = "unavailable";
                    return false;
                }

                sClipCallback = Delegate.CreateDelegate(simCallback, typeof(SacsRecorder).GetMethod("OnClipHandle", flags));
                return true;
            }
            catch (Exception ex)
            {
                sClipsUnavailable = "error=" + ex.GetType().Name;
                return false;
            }
        }

        private static IntPtr AsHandle(object data)
        {
            if (data is IntPtr)
            {
                return (IntPtr)data;
            }

            if (data is int)
            {
                return new IntPtr((int)data);
            }

            if (data is uint)
            {
                return new IntPtr((int)(uint)data);
            }

            return data is long ? new IntPtr((long)data) : data is ulong ? new IntPtr((long)(ulong)data) : IntPtr.Zero;
        }

        private static void NoteActiveClips(ulong actor, Driver driver, uint state)
        {
            IntPtr handle = IntPtr.Zero;
            string text = string.Empty;
            int count = 0;
            try
            {
                if (!ClipNatives())
                {
                    text = " " + sClipsUnavailable;
                }
                else
                {
                    sClipHandle = null;
                    count = (int)sClipCount.Invoke(null, new object[] { actor, sClipCallback });
                    handle = AsHandle(sClipHandle);
                    text = " data=" + (sClipHandle == null ? "null" : sClipHandle.GetType().Name);
                    for (uint i = 0u; handle != IntPtr.Zero && i < (uint)count && i < 16u; i++)
                    {
                        object[] args = new object[] { handle, i, 0u };
                        string name = sClipInfo.Invoke(null, args) as string;
                        text += (i == 0u ? " " : ",") + (name ?? "?") + "@" + U((uint)args[2]);
                    }
                }
            }
            catch (Exception ex)
            {
                text += " error=" + (ex.InnerException ?? ex).GetType().Name;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        sClipDestroy.Invoke(null, new object[] { handle });
                    }
                    catch (Exception)
                    {
                        SwallowedErrors.Note();
                    }
                }
            }

            EventNotes++;
            MotionWatch.Note(actor, "sacs clips " + driver.Machine.ToString("x16", CultureInfo.InvariantCulture) + ":" + U(state)
                + " n=" + count.ToString(CultureInfo.InvariantCulture) + text);
        }

        // probe: does SACS take a request to a loop hub's private variant? The tunable
        // sacs.steer.<hub hash>.<variant hash> (8 hex digits each; a hash is too large for a tune's value) set to 1 makes
        // this game request that variant each time a machine enters the hub, for the actor "x", and note the result for
        // the motion watch:
        //   sacs steer <machine hex>:<hub> -> <variant> actor=<hash> result=<code>
        // The next hub cycle's length (scripts/sacs_timing.py variants) or the clips note shows whether the game took it.
        // Live (2026-10-18, shower): kSuccess and the variant's clip plays, but the machine then idles in the variant and
        // a later exit request is not served, so this is a probe, not a fix. Unset (the default) changes nothing.
        private static readonly uint ActorX = ResourceUtils.HashString32("x");

        // The SACS hooks' tunables, read again only after a tune changes (Tunables.Version). these hooks run on
        // game tasks' stacks on every yes/no SetParam and SACS event, and while any tune was set they built a name and
        // walked or hashed the tunables on each call (CachedTunable says why that matters).
        private static readonly CachedTunable sObserveClips = new CachedTunable("sacs.observe-clips", 0f);
        private static int sTunableMapsVersion = -1;
        private static readonly Dictionary<uint, SteerSetting> sSteer = new Dictionary<uint, SteerSetting>();
        private static readonly Dictionary<uint, SteerSetting> sSteerQueue = new Dictionary<uint, SteerSetting>();
        private static readonly Dictionary<uint, double> sForced = new Dictionary<uint, double>();

        // sacs.steer[-queue].<hub>.<variant> for one hub: the first such tunable in Tunables' order, as TryGetSetPrefix
        // found it, with whether its variant parsed.
        private sealed class SteerSetting
        {
            internal uint Variant;
            internal bool Parsed;
            internal double On;
        }

        private static void SyncTunableMaps()
        {
            int version = Tunables.Version;
            if (sTunableMapsVersion == version)
            {
                return;
            }

            sTunableMapsVersion = version;
            sSteer.Clear();
            sSteerQueue.Clear();
            sForced.Clear();
            if (!Tunables.AnySet)
            {
                return;
            }

            foreach (KeyValuePair<string, double> entry in Tunables.SetEntries())
            {
                AddSteer(sSteer, "sacs.steer.", entry.Key, entry.Value);
                AddSteer(sSteerQueue, "sacs.steer-queue.", entry.Key, entry.Value);
                uint param;
                if (entry.Key.Length == "sacs.force.".Length + 8 && entry.Key.StartsWith("sacs.force.", StringComparison.Ordinal)
                    && TryHex8(entry.Key.Substring("sacs.force.".Length), out param))
                {
                    sForced[param] = entry.Value;
                }
            }
        }

        // The names the prefix prefix + hub.ToString("x8") + "." matched.
        private static void AddSteer(Dictionary<uint, SteerSetting> map, string prefix, string name, double on)
        {
            uint hub;
            if (name.Length < prefix.Length + 9 || !name.StartsWith(prefix, StringComparison.Ordinal) || name[prefix.Length + 8] != '.'
                || !TryHex8(name.Substring(prefix.Length, 8), out hub) || map.ContainsKey(hub))
            {
                return;
            }

            SteerSetting setting = new SteerSetting();
            setting.On = on;
            setting.Parsed = uint.TryParse(name.Substring(prefix.Length + 9), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out setting.Variant);
            map[hub] = setting;
        }

        // Exactly the text value.ToString("x8") gives: 8 lower-case hex digits.
        private static bool TryHex8(string text, out uint value)
        {
            return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
                && value.ToString("x8", CultureInfo.InvariantCulture) == text;
        }

        private static void Steer(uint driverHandle, Driver driver, uint hub)
        {
            SyncTunableMaps();
            SteerSetting setting;
            if (sSteer.Count == 0 || !sSteer.TryGetValue(hub, out setting) || setting.On <= 0 || !setting.Parsed)
            {
                return;
            }

            uint variant = setting.Variant;

            uint actorHash = ActorX;
            ulong actor;
            if (!driver.Actors.TryGetValue(actorHash, out actor))
            {
                return;
            }

            ISacsProxy proxy = Current;
            string result;
            try
            {
                result = proxy == null ? "no-proxy"
                    : proxy.RequestState(false, driverHandle, actorHash, variant, (DriverRequestFlags)0u, 0f, 0u, 0u, 0u).ToString();
            }
            catch (Exception ex)
            {
                result = "error=" + ex.GetType().Name;
            }

            EventNotes++;
            MotionWatch.Note(actor, "sacs steer " + driver.Machine.ToString("x16", CultureInfo.InvariantCulture) + ":" + U(hub) + " -> "
                + U(variant) + " actor=" + U(actorHash) + " result=" + result);
        }

        // probe, queue steering (docs/engine/sacs-native-picks.md section 4, "Inferred, untested: queue steering"):
        // the tunable sacs.steer-queue.<hub hash>.<variant hash> (8 hex digits each) set to 1 makes this game, each time a
        // machine enters the hub, queue two requests for the actor "x" without kClearQueue: the variant (0x40, the
        // walker's default flags), then the hub itself (0x40 | kRequestAllowRequestForCurrentState). The walker should go
        // to the variant, return to the hub and loop on, rather than idle in the variant as the plain sacs.steer does,
        // and a later exit request should still be served. Both results go to the motion watch:
        //   sacs steer-queue <machine hex>:<hub> -> <variant> actor=<hash> variant=<code> hub=<code>
        // The requests go through Current (this recorder), so a server's are streamed as R| lines and its clients'
        // copies replay them: set it on the server only. Unset (the default) changes nothing.
        private const DriverRequestFlags QueueSteerVariantFlags = (DriverRequestFlags)0x40u;
        private const DriverRequestFlags QueueSteerHubFlags = (DriverRequestFlags)0x140u;

        private static void SteerQueue(uint driverHandle, Driver driver, uint hub)
        {
            SyncTunableMaps();
            SteerSetting setting;
            if (sSteerQueue.Count == 0 || !sSteerQueue.TryGetValue(hub, out setting) || setting.On <= 0 || !setting.Parsed)
            {
                return;
            }

            uint variant = setting.Variant;

            uint actorHash = ActorX;
            ulong actor;
            if (!driver.Actors.TryGetValue(actorHash, out actor))
            {
                return;
            }

            ISacsProxy proxy = Current;
            string variantResult;
            string hubResult;
            try
            {
                if (proxy == null)
                {
                    variantResult = hubResult = "no-proxy";
                }
                else
                {
                    variantResult = proxy.RequestState(false, driverHandle, actorHash, variant, QueueSteerVariantFlags, 0f, 0u, 0u, 0u).ToString();
                    hubResult = proxy.RequestState(false, driverHandle, actorHash, hub, QueueSteerHubFlags, 0f, 0u, 0u, 0u).ToString();
                }
            }
            catch (Exception ex)
            {
                variantResult = hubResult = "error=" + ex.GetType().Name;
            }

            EventNotes++;
            MotionWatch.Note(actor, "sacs steer-queue " + driver.Machine.ToString("x16", CultureInfo.InvariantCulture) + ":" + U(hub) + " -> "
                + U(variant) + " actor=" + U(actorHash) + " variant=" + variantResult + " hub=" + hubResult);
        }

        public event VoidEventHandler PreSave
        {
            add { mInner.PreSave += value; }
            remove { mInner.PreSave -= value; }
        }

        public uint AcquireDriver(bool allowYeld, ObjectGuid hostScriptObjectId, ResourceKey stateMachineKey, int priority)
        {
            uint handle = mInner.AcquireDriver(allowYeld, hostScriptObjectId, stateMachineKey, priority);
            try
            {
                Calls++;
                Driver driver = new Driver();
                driver.Host = hostScriptObjectId.Value;
                driver.Machine = stateMachineKey.InstanceId;
                driver.Cosmetic = IsCosmeticMachine(driver.Machine);
                driver.Type = stateMachineKey.TypeId;
                driver.Group = stateMachineKey.GroupId;
                driver.Priority = priority;
                driver.Origin = AcquireOrigin();
                sDrivers[handle] = driver;
                Remember(handle, "A|" + U(handle) + "|" + U(driver.Host) + "|" + U(stateMachineKey.TypeId) + "|" + U(stateMachineKey.GroupId) + "|"
                    + U(stateMachineKey.InstanceId) + "|" + priority.ToString(CultureInfo.InvariantCulture));
                SacsSeedCopy.NoteAcquired(handle, RingEnabled && !driver.Cosmetic, SacsSeedCopy.On, false);
                if (RingEnabled && !driver.Cosmetic)
                {
                    Ring("A|" + U(handle) + "|" + U(driver.Host) + "|" + U(stateMachineKey.TypeId) + "|" + U(stateMachineKey.GroupId) + "|"
                        + U(stateMachineKey.InstanceId) + "|" + priority.ToString(CultureInfo.InvariantCulture));
                    if (SacsSeedCopy.On)
                    {
                        SendSeeds(handle, driver);
                    }
                    else if (SacsSeedCopy.ReadLoop > 0)
                    {
                        SacsSeedCopy.ReadOnly(handle);
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return handle;
        }

        public void ReleaseDriver(uint driverHandle)
        {
            try
            {
                Calls++;
                Driver released;
                bool cosmetic = false;
                if (sDrivers.TryGetValue(driverHandle, out released))
                {
                    cosmetic = released.Cosmetic;
                    sDrivers.Remove(driverHandle);
                    ForgetReleased(driverHandle, released);
                }

                sParams.Forget(driverHandle);
                if (RingEnabled && !cosmetic) Ring("X|" + U(driverHandle));
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            mInner.ReleaseDriver(driverHandle);
        }

        // The actors whose shown state came from a released driver fall back to their newest state on a live driver.
        private static void ForgetReleased(uint handle, Driver released)
        {
            foreach (ulong actor in released.LastStates.Keys)
            {
                ActorState shown;
                if (!Actors.TryGetValue(actor, out shown) || shown.Driver != handle)
                {
                    continue;
                }

                ActorState best = null;
                foreach (Driver live in sDrivers.Values)
                {
                    ActorState candidate;
                    if (live.LastStates.TryGetValue(actor, out candidate) && (best == null || candidate.Sequence > best.Sequence))
                    {
                        best = candidate;
                    }
                }

                if (best == null)
                {
                    Actors.Remove(actor);
                }
                else
                {
                    shown.Machine = best.Machine;
                    shown.State = best.State;
                    shown.Sequence = best.Sequence;
                    shown.Driver = best.Driver;
                }
            }
        }

        public bool IsEventQueueValid(uint driverHandle)
        {
            return mInner.IsEventQueueValid(driverHandle);
        }

        public void SetEventQueue(uint driverHandle, uint eventQueueHandle)
        {
            mInner.SetEventQueue(driverHandle, eventQueueHandle);
            try
            {
                if (sQueueDrivers.Count >= 8192)
                {
                    sQueueDrivers.Clear();
                }

                sQueueDrivers[eventQueueHandle] = driverHandle;
                Driver driver;
                if (sDrivers.TryGetValue(driverHandle, out driver))
                {
                    driver.Observed = EventQueueRecorder.Owns(eventQueueHandle);
                    driver.Entered.Clear();
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        public void SetActor(uint driverHandle, uint actorHash, ObjectGuid actorObjectId)
        {
            try
            {
                Calls++;
                Driver driver;
                if (sDrivers.TryGetValue(driverHandle, out driver))
                {
                    driver.Actors[actorHash] = actorObjectId.Value;
                    Remember(driverHandle, "S|" + U(driverHandle) + "|" + U(actorHash) + "|" + U(actorObjectId.Value));
                }

                if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring("S|" + U(driverHandle) + "|" + U(actorHash) + "|" + U(actorObjectId.Value));
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            mInner.SetActor(driverHandle, actorHash, actorObjectId);
        }

        public bool HasActor(uint driverHandle, uint actorHash)
        {
            return mInner.HasActor(driverHandle, actorHash);
        }

        // Dev A/B of a jazz branch: the tunable sacs.force.<parameter hash, 8 hex digits> set to 1 forces that
        // yes/no parameter to yes, 0 to no, on every machine in this game; on the server the stream carries the forced
        // value, so the clients' copies take the same branch. Unset (the default) changes nothing.
        private const uint ParamYes = 979470758u;
        private const uint ParamNo = 1668749452u;

        private static uint ForcedParam(uint paramHash, uint valueHash)
        {
            if (valueHash != ParamYes && valueHash != ParamNo)
            {
                return valueHash;
            }

            SyncTunableMaps();
            double forced;
            if (sForced.Count == 0 || !sForced.TryGetValue(paramHash, out forced))
            {
                return valueHash;
            }

            return forced > 0 ? ParamYes : ParamNo;
        }

        // item 4: each SetParam is held when it is the copy's own on a driver the replay drives (HoldOwn), and
        // otherwise also noted as the driver's latest value for that parameter (NoteParam).
        public void SetParam(uint driverHandle, uint paramHash, uint valueHash)
        {
            if (HoldOwn(driverHandle, ParamAuthority.HashName(paramHash)))
            {
                return;
            }

            valueHash = ForcedParam(paramHash, valueHash);
            string line = "P|" + U(driverHandle) + "|" + U(paramHash) + "|" + U(valueHash);
            Remember(driverHandle, line);
            NoteParam(driverHandle, line);
            if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring(line);
            mInner.SetParam(driverHandle, paramHash, valueHash);
        }

        public void SetParam(uint driverHandle, uint paramHash, string value)
        {
            if (HoldOwn(driverHandle, ParamAuthority.HashName(paramHash)))
            {
                return;
            }

            string line = "Ps|" + U(driverHandle) + "|" + U(paramHash) + "|" + (value ?? "");
            Remember(driverHandle, line);
            NoteParam(driverHandle, line);
            if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring(line);
            mInner.SetParam(driverHandle, paramHash, value);
        }

        public void SetParam(uint driverHandle, string paramPrefix, string paramName, string value)
        {
            if (HoldOwn(driverHandle, (paramPrefix ?? "") + ":" + (paramName ?? "")))
            {
                return;
            }

            string line = "Pn|" + U(driverHandle) + "|" + (paramPrefix ?? "") + "|" + (paramName ?? "") + "|" + (value ?? "");
            Remember(driverHandle, line);
            NoteParam(driverHandle, line);
            if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring(line);
            mInner.SetParam(driverHandle, paramPrefix, paramName, value);
        }

        public void SetParam(uint driverHandle, string paramPrefix, string paramName, uint valueHash)
        {
            if (HoldOwn(driverHandle, (paramPrefix ?? "") + ":" + (paramName ?? "")))
            {
                return;
            }

            string line = "Ph|" + U(driverHandle) + "|" + (paramPrefix ?? "") + "|" + (paramName ?? "") + "|" + U(valueHash);
            Remember(driverHandle, line);
            NoteParam(driverHandle, line);
            if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring(line);
            mInner.SetParam(driverHandle, paramPrefix, paramName, valueHash);
        }

        public uint GetParam(uint driverHandle, uint paramHash)
        {
            return mInner.GetParam(driverHandle, paramHash);
        }

        public string GetParamString(uint driverHandle, uint paramHash)
        {
            return mInner.GetParamString(driverHandle, paramHash);
        }

        public uint GetNumParams(uint driverHandle)
        {
            return mInner.GetNumParams(driverHandle);
        }

        public string GetParamNameByIndex(uint driverHandle, uint paramIndex)
        {
            return mInner.GetParamNameByIndex(driverHandle, paramIndex);
        }

        public string GetDebugString(ulong scriptObjectId)
        {
            return mInner.GetDebugString(scriptObjectId);
        }

        public void EnterState(uint driverHandle, uint actorHash, uint stateHash)
        {
            RecordState(driverHandle, actorHash, stateHash, "E", 0u);
            mInner.EnterState(driverHandle, actorHash, stateHash);
            if (RepairLabel.IsRepair(LocalContext))
            {
                ServerHostMain.NoteRepairEntry(driverHandle, stateHash); // B2+(a): RepairEntryGate's mark
            }
        }

        public ResultCodes RequestState(bool yield, uint driverHandle, uint actorHash, uint stateHash, DriverRequestFlags flags, float wakeupTimeOffset, uint timeoutInSimMinutes, uint bridgeDriverHandle, uint bridgeActorHash)
        {
            // Every argument. The bridge (bridgeDriverHandle, bridgeActorHash) is how a posture's machine hands the Sim to the
            // next one: the next machine's request waits on the bridge (kRequestWaitOnBridge) and BridgeOrigin.BridgeTo then
            // requests the posture machine's exit state naming it. Without the bridge a client's copy of the next machine
            // waited for good (a phone held at the hip, arms down: cold-test-097).
            RecordState(driverHandle, actorHash, stateHash, "R", (uint)flags, "|" + wakeupTimeOffset.ToString("R", CultureInfo.InvariantCulture) + "|"
                + U(timeoutInSimMinutes) + "|" + U(bridgeDriverHandle) + "|" + U(bridgeActorHash));
            bool trace = RequestTrace.Enabled;
            string before = "";
            long sequence = Sequence;
            string origin = Replaying ? "replay" : (LocalContext ?? "game");
            if (trace)
            {
                try { before = RequestSnapshot(driverHandle, actorHash); }
                catch (Exception ex) { before = "unavailable:" + ex.GetType().Name; }
            }
            ResultCodes result = mInner.RequestState(yield, driverHandle, actorHash, stateHash, flags, wakeupTimeOffset, timeoutInSimMinutes, bridgeDriverHandle, bridgeActorHash);
            if (trace)
            {
                try
                {
                    CultureInfo c = CultureInfo.InvariantCulture;
                    RequestTrace.Add("utc=" + DateTime.UtcNow.ToString("HH:mm:ss.fff", c) + "|seq=" + sequence.ToString(c)
                        + "|driver=" + U(driverHandle) + "|actor=" + U(actorHash) + "|state=" + U(stateHash)
                        + "|flags=" + U((uint)flags) + "|bridge=" + U(bridgeDriverHandle) + "/" + U(bridgeActorHash)
                        + "|yield=" + (yield ? "1" : "0") + "|origin=" + origin
                        + "|result=" + result.ToString() + "|before=" + before, (int)result);
                }
                catch (Exception) { SwallowedErrors.Note(); }
            }
            return result;
        }

        public void Interrupt(uint driverHandle, uint actorHash)
        {
            Remember(driverHandle, "I|" + U(driverHandle) + "|" + U(actorHash));
            if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring("I|" + U(driverHandle) + "|" + U(actorHash));
            mInner.Interrupt(driverHandle, actorHash);
        }

        public bool CanPause(uint driverHandle, uint actorHash)
        {
            return mInner.CanPause(driverHandle, actorHash);
        }

        public bool Pause(uint driverHandle, uint actorHash)
        {
            bool paused = mInner.Pause(driverHandle, actorHash);
            if (paused)
            {
                Remember(driverHandle, "Pa|" + U(driverHandle) + "|" + U(actorHash));
                if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring("Pa|" + U(driverHandle) + "|" + U(actorHash));
            }

            return paused;
        }

        public void Continue(uint driverHandle, uint actorHash)
        {
            Remember(driverHandle, "Co|" + U(driverHandle) + "|" + U(actorHash));
            if (RingEnabled && !IsCosmeticDriver(driverHandle)) Ring("Co|" + U(driverHandle) + "|" + U(actorHash));
            mInner.Continue(driverHandle, actorHash);
        }

        public void UpdateEventFilter(uint driverHandle, uint eventType, uint eventSubType, uint eventId, EventKeyOperation operation)
        {
            mInner.UpdateEventFilter(driverHandle, eventType, eventSubType, eventId, operation);
        }

        public int GetActiveClipCount(ObjectGuid objGuid, out IntPtr clipsHandle)
        {
            return mInner.GetActiveClipCount(objGuid, out clipsHandle);
        }

        public ClipInfo GetActiveClipInfo(IntPtr clipsHandle, uint clipIndex)
        {
            return mInner.GetActiveClipInfo(clipsHandle, clipIndex);
        }

        public void DestroyClipInfoHandle(IntPtr clipsHandle)
        {
            mInner.DestroyClipInfoHandle(clipsHandle);
        }

        public uint GetDefaultPriority(uint driverHandle)
        {
            return mInner.GetDefaultPriority(driverHandle);
        }

        public int StopAllAnimation(ObjectGuid objGuid)
        {
            if (RingEnabled) Ring("Z|" + U(objGuid.Value));
            return mInner.StopAllAnimation(objGuid);
        }

        public int SetRig(ObjectGuid objGuid, ResourceKey rigKey)
        {
            if (RingEnabled) Ring("Rg|" + U(objGuid.Value) + "|" + U(rigKey.InstanceId) + "~" + U(rigKey.TypeId) + "~" + U(rigKey.GroupId));
            return mInner.SetRig(objGuid, rigKey);
        }

        public void SetClipInterruptible(ObjectGuid objGuid, AnimationPriority priority, bool bInterruptible)
        {
            if (RingEnabled) Ring("Ci|" + U(objGuid.Value) + "|" + ((int)priority).ToString(CultureInfo.InvariantCulture) + "|" + (bInterruptible ? "1" : "0"));
            mInner.SetClipInterruptible(objGuid, priority, bInterruptible);
        }
    }
}
