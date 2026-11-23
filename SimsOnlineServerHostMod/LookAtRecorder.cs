using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // The head look-at boundary (internal documentation, seam-gaps row 2). Every look-at a script starts goes
    // through Sims3.SimIFace.LookAt.CreateLookAtTask, whose implementation is read once from AppDomain data ("LookAtMgr")
    // into the private static LookAt.gLookAtMgr (ScriptCore.LookAtMgr). LookAtManager (per Sim: interaction, reaction,
    // environment, routing and group-talk look-ats) creates a task, sets its target and tuning, then enables it
    // (LookAtManager.CreateLookAt, LookAtManager.cs:559-578), and stops it when a newer one replaces it (SetLookAt :535,
    // ClearLookAt :591). This decorator is swapped in there on the server; the tasks it hands out record:
    //   Lk|sim|e|task|priority|filter|target|slotHash|towards|away|tilt|curve|pitch|yaw|torsoThr|torsoPct|frustum|limits|blend|ox~oy~oz
    //                                                       EnableIt, with every value set before it
    //   Lk|sim|e|...                                        the same again when a value changes on an enabled task
    //   Lk|sim|x|task|0/1                                   StopIt (hard), or Dispose
    // A client plays the same on its copy of the Sim (a puppet only: PresenterHook.SkipLookAt refuses the copy's own
    // LookAtManager there), mapping the target to its copy. Look-at tasks hold no reference here: a task the game drops
    // is finalized as before (the native task is destroyed by ScriptCore.LookAtTask's finalizer).
    internal sealed class LookAtRecorder : ILookAt
    {
        private readonly ILookAt mInner;
        private static uint sNextTask;
        internal static long Lines;
        internal static long Tasks;

        private LookAtRecorder(ILookAt inner)
        {
            mInner = inner;
        }

        internal static FieldInfo HolderField()
        {
            return typeof(LookAt).GetField("gLookAtMgr", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = HolderField();
                return field != null && field.GetValue(null) is LookAtRecorder;
            }
        }

        internal static string Install()
        {
            try
            {
                FieldInfo field = HolderField();
                if (field == null)
                {
                    return "lookat=unavailable";
                }

                object current = field.GetValue(null);
                if (current is LookAtRecorder)
                {
                    return "lookat=on";
                }

                ILookAt inner = current as ILookAt;
                if (inner == null)
                {
                    return "lookat=no-instance";
                }

                field.SetValue(null, new LookAtRecorder(inner));
                return "lookat=on";
            }
            catch (Exception ex)
            {
                return "lookat=error:" + ex.GetType().Name;
            }
        }

        // The implementation under the recorder (the client's own, to replay onto).
        internal static ILookAt Inner()
        {
            FieldInfo field = HolderField();
            object current = field == null ? null : field.GetValue(null);
            LookAtRecorder recorder = current as LookAtRecorder;
            return recorder != null ? recorder.mInner : current as ILookAt;
        }

        public ILookAtTask CreateLookAtTask(IScriptProxy simObj, int priority, LookAtJointFilter jointFilter)
        {
            ILookAtTask inner = mInner.CreateLookAtTask(simObj, priority, jointFilter);
            try
            {
                ulong sim = ((ITask)simObj).ObjectId.Value;
                if (inner != null && sim != 0uL)
                {
                    Tasks++;
                    return new RecordedTask(inner, sim, ++sNextTask, priority, (int)jointFilter);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return inner;
        }

        public int FlushLookAtQueue(IScriptProxy simObj, int priority)
        {
            return mInner.FlushLookAtQueue(simObj, priority);
        }

        private static void Record(string line, bool live)
        {
            try
            {
                if (MotionWatch.Active)
                {
                    MotionWatch.NoteCall(line);
                }

                if (SacsRecorder.RingEnabled)
                {
                    Lines++;
                    SacsRecorder.RingLine(line);
                    // For a late joiner (BoundaryState): an enabled look-at, until it stops.
                    if (live)
                    {
                        string[] e = line.Split('|');
                        Seen(ulong.Parse(e[1], CultureInfo.InvariantCulture), ulong.Parse(e[6], CultureInfo.InvariantCulture));
                        BoundaryState.Remember(line, 4);
                    }
                    else
                    {
                        string[] f = line.Split('|');
                        BoundaryState.ForgetKey("Lk|" + f[1] + "|e|" + f[3]);
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // A server look-at task: every member passes through; EnableIt, a change while enabled, StopIt and Dispose record.
        private sealed class RecordedTask : ILookAtTask
        {
            private readonly ILookAtTask mTask;
            private readonly ulong mSim;
            private readonly uint mId;
            private readonly int mPriority;
            private readonly int mFilter;
            private bool mEnabled;
            private uint mBlend;

            internal RecordedTask(ILookAtTask task, ulong sim, uint id, int priority, int filter)
            {
                mTask = task;
                mSim = sim;
                mId = id;
                mPriority = priority;
                mFilter = filter;
            }

            private string Prefix(string op)
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                return "Lk|" + mSim.ToString(c) + "|" + op + "|" + mId.ToString(c);
            }

            private void Enabled()
            {
                try
                {
                    CultureInfo c = CultureInfo.InvariantCulture;
                    Vector3 o = mTask.TargetOffsetVector;
                    Record(Prefix("e") + "|" + mPriority.ToString(c) + "|" + mFilter.ToString(c) + "|" + mTask.TargetObjectId.ToString(c)
                        + "|" + mTask.TargetSlotNameHash.ToString(c) + "|" + F(mTask.LookTowardsSpeed) + "|" + F(mTask.LookAwaySpeed)
                        + "|" + F(mTask.RightTiltness) + "|" + F(mTask.LookTowardsCurveRate) + "|" + F(mTask.AttenuatorPitch)
                        + "|" + F(mTask.AttenuatorYaw) + "|" + F(mTask.TorsoThreshold) + "|" + F(mTask.TorsoPercent)
                        + "|" + (mTask.IgnoreFrustum ? "1" : "0") + "|" + (mTask.HoldAtJointLimits ? "1" : "0") + "|" + mBlend.ToString(c)
                        + "|" + F(o.x) + "~" + F(o.y) + "~" + F(o.z), true);
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }

            private void Changed()
            {
                if (mEnabled)
                {
                    Enabled();
                }
            }

            public ulong TargetObjectId
            {
                get { return mTask.TargetObjectId; }
                set { mTask.TargetObjectId = value; Changed(); }
            }

            public uint TargetSlotNameHash
            {
                get { return mTask.TargetSlotNameHash; }
                set { mTask.TargetSlotNameHash = value; Changed(); }
            }

            public Vector3 TargetOffsetVector
            {
                get { return mTask.TargetOffsetVector; }
                set { mTask.TargetOffsetVector = value; Changed(); }
            }

            public float LookTowardsSpeed
            {
                get { return mTask.LookTowardsSpeed; }
                set { mTask.LookTowardsSpeed = value; Changed(); }
            }

            public float LookAwaySpeed
            {
                get { return mTask.LookAwaySpeed; }
                set { mTask.LookAwaySpeed = value; Changed(); }
            }

            public bool IgnoreFrustum
            {
                get { return mTask.IgnoreFrustum; }
                set { mTask.IgnoreFrustum = value; Changed(); }
            }

            public bool HoldAtJointLimits
            {
                get { return mTask.HoldAtJointLimits; }
                set { mTask.HoldAtJointLimits = value; Changed(); }
            }

            public uint BlendInFrames
            {
                set { mTask.BlendInFrames = value; mBlend = value; }
            }

            public Delegate EventHandler
            {
                set { mTask.EventHandler = value; } // the server's own callbacks; not streamed
            }

            public float RightTiltness
            {
                get { return mTask.RightTiltness; }
                set { mTask.RightTiltness = value; Changed(); }
            }

            public float TorsoThreshold
            {
                get { return mTask.TorsoThreshold; }
                set { mTask.TorsoThreshold = value; Changed(); }
            }

            public float TorsoPercent
            {
                get { return mTask.TorsoPercent; }
                set { mTask.TorsoPercent = value; Changed(); }
            }

            public float AttenuatorPitch
            {
                get { return mTask.AttenuatorPitch; }
                set { mTask.AttenuatorPitch = value; Changed(); }
            }

            public float AttenuatorYaw
            {
                get { return mTask.AttenuatorYaw; }
                set { mTask.AttenuatorYaw = value; Changed(); }
            }

            public float LookTowardsCurveRate
            {
                get { return mTask.LookTowardsCurveRate; }
                set { mTask.LookTowardsCurveRate = value; Changed(); }
            }

            public bool EnableIt()
            {
                bool result = mTask.EnableIt();
                mEnabled = true;
                Enabled();
                return result;
            }

            public void StopIt(bool hardStop)
            {
                mTask.StopIt(hardStop);
                if (mEnabled)
                {
                    mEnabled = false;
                    Record(Prefix("x") + "|" + (hardStop ? "1" : "0"), false);
                }
            }

            public void Dispose()
            {
                if (mEnabled)
                {
                    mEnabled = false;
                    Record(Prefix("x") + "|1", false);
                }

                mTask.Dispose();
            }
        }

        // Client side: the server's task (sim and id) to this client's own task on its copy.
        private static readonly Dictionary<string, ILookAtTask> sClientTasks = new Dictionary<string, ILookAtTask>();
        private static readonly Dictionary<ulong, ulong> sClientTargetOf = new Dictionary<ulong, ulong>();
        internal static long Replayed;
        internal static long Stopped;
        internal static long NotPuppet;
        internal static long ReplayFailed;

        // This client's copy's look-at target (its local id), from the stream: 0 when none. The look-at compare probe
        // reads it (scratch/LookAtCompareProbe.cs).
        public static ulong ClientTargetOf(ulong localSim)
        {
            ulong target;
            return sClientTargetOf.TryGetValue(localSim, out target) ? target : 0uL;
        }

        // When a Sim last looked at a target (System.Environment.TickCount; this game's ids): enabled on the server, replayed on a
        // client. A chat's look-at lasts only its animation, so the compare probe asks "within the last seconds" instead
        // of sampling one instant.
        private static readonly Dictionary<string, int> sSeen = new Dictionary<string, int>();

        private static void Seen(ulong sim, ulong target)
        {
            if (sSeen.Count > 4096)
            {
                sSeen.Clear();
            }

            sSeen[sim.ToString(CultureInfo.InvariantCulture) + "|" + target.ToString(CultureInfo.InvariantCulture)] = System.Environment.TickCount;
        }

        // Milliseconds since this Sim's last look-at on the target, or -1 if none.
        public static int MsSinceLookAt(ulong sim, ulong target)
        {
            int at;
            if (!sSeen.TryGetValue(sim.ToString(CultureInfo.InvariantCulture) + "|" + target.ToString(CultureInfo.InvariantCulture), out at))
            {
                return -1;
            }

            return unchecked(System.Environment.TickCount - at);
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "installed:" + (Installed ? "1" : "0") + ",lines:" + Lines.ToString(c) + ",client:replayed:" + Replayed.ToString(c)
                + "/stopped:" + Stopped.ToString(c) + "/live:" + sClientTasks.Count.ToString(c) + "/notPuppet:" + NotPuppet.ToString(c)
                + "/failed:" + ReplayFailed.ToString(c);
        }

        // Lk|sim|e|task|... and Lk|sim|x|task|hard on this client's copy. isPuppet: the gate's complement (only a copy whose
        // own LookAtManager the fork refuses gets the server's look-ats; a Sim this player owns keeps its own).
        internal static bool Replay(string[] f, Converter<ulong, ObjectGuid> map, Predicate<ulong> isPuppet)
        {
            try
            {
                if (f.Length < 5)
                {
                    return false;
                }

                CultureInfo c = CultureInfo.InvariantCulture;
                ObjectGuid sim = map(ulong.Parse(f[2], c));
                string key = f[2] + "|" + f[4];
                if (f[3] == "x")
                {
                    ILookAtTask old;
                    if (sClientTasks.TryGetValue(key, out old))
                    {
                        sClientTasks.Remove(key);
                        old.StopIt(f.Length > 5 && f[5] == "1");
                        old.Dispose();
                        Stopped++;
                        if (sim.IsValid)
                        {
                            sClientTargetOf.Remove(sim.Value);
                        }
                    }

                    return true;
                }

                if (f[3] != "e" || f.Length < 20 || !sim.IsValid)
                {
                    return false;
                }

                if (!isPuppet(sim.Value))
                {
                    NotPuppet++;
                    return true;
                }

                ObjectGuid target = map(ulong.Parse(f[7], c));
                ILookAtTask task;
                if (!sClientTasks.TryGetValue(key, out task))
                {
                    IScriptProxy proxy = Simulator.GetProxy(sim);
                    ILookAt own = Inner();
                    if (proxy == null || own == null)
                    {
                        ReplayFailed++;
                        return false;
                    }

                    if (sClientTasks.Count >= 512)
                    {
                        foreach (ILookAtTask stale in sClientTasks.Values)
                        {
                            stale.Dispose();
                        }

                        sClientTasks.Clear();
                        sClientTargetOf.Clear();
                    }

                    task = own.CreateLookAtTask(proxy, int.Parse(f[5], c), (LookAtJointFilter)int.Parse(f[6], c));
                    if (task == null)
                    {
                        ReplayFailed++;
                        return false;
                    }

                    sClientTasks[key] = task;
                }

                task.TargetObjectId = target.IsValid ? target.Value : 0uL;
                task.TargetSlotNameHash = uint.Parse(f[8], c);
                task.LookTowardsSpeed = float.Parse(f[9], c);
                task.LookAwaySpeed = float.Parse(f[10], c);
                task.RightTiltness = float.Parse(f[11], c);
                task.LookTowardsCurveRate = float.Parse(f[12], c);
                task.AttenuatorPitch = float.Parse(f[13], c);
                task.AttenuatorYaw = float.Parse(f[14], c);
                task.TorsoThreshold = float.Parse(f[15], c);
                task.TorsoPercent = float.Parse(f[16], c);
                task.IgnoreFrustum = f[17] == "1";
                task.HoldAtJointLimits = f[18] == "1";
                task.BlendInFrames = uint.Parse(f[19], c);
                if (f.Length > 20)
                {
                    string[] o = f[20].Split('~');
                    if (o.Length == 3)
                    {
                        task.TargetOffsetVector = new Vector3(float.Parse(o[0], c), float.Parse(o[1], c), float.Parse(o[2], c));
                    }
                }

                task.EnableIt();
                sClientTargetOf[sim.Value] = target.IsValid ? target.Value : 0uL;
                Seen(sim.Value, target.IsValid ? target.Value : 0uL);
                Replayed++;
                return true;
            }
            catch (Exception)
            {
                ReplayFailed++;
                SwallowedErrors.Note();
                return false;
            }
        }
    }
}
