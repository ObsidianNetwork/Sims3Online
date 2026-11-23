using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // The sound boundary (docs/architecture-deep-dive/boundary-seams.md). Sounds a script plays on an object go through
    // Sims3.SimIFace.Audio, whose implementation is read once from AppDomain data ("Audio") into the private static
    // Audio.gAudio. This decorator is swapped in there on the server and records the object sounds:
    //   Au|object|o|handle|hexName|loop        StartObjectSound
    //   Au|object|w|handle|hexName|flags       StartObjectSoundWithFlags
    //   Au|object|x|handle                     StopObjectSound / StopSound of an object sound
    //   Au|object|z                            StopAllObjectSound
    //   Au|object|u|0/1                        MuteAllObjectSounds
    //   Au|object|s|handle|key|value           SetSoundProperty of an object sound
    //   Au|object|p|target  g|target  P  G     put / get sounds (on a target, on the floor)
    //   Au|0|t|handle|x|y|z|hexName            StartSound at a point in the world (puddles, a career's music, a car's
    //   Au|0|x|handle  Au|0|s|handle|key|value  chirp; seam-gaps row 4): object 0, sent near the streamed Sims
    // A client plays the same on its copy and keeps its own handle for the server's. Sounds from animation events play
    // with the replayed animation. Not sent: interface sounds and music (each player's own), and the point sounds each
    // game makes itself (such as thunder from the weather or a venue's ambience from its VenueAmbienceManager).
    // Stings (sting_*: career, baby, death, wishes) are routed to the owning player:
    // one on a Sim (Sim.ShowTNSAndPlayStingIfSelectable, Play3dStingIfSelectable: an object sound on the Sim) or a bare
    // StartSound run on a Sim's own task goes to the player who claims that Sim (or its household), as a remote dialog
    // of kind Sting (ServerHostMain.PumpStings); a sting with no claimed Sim to own it stays on the server (Unowned).
    internal sealed class AudioRecorder : IAudio
    {
        private readonly IAudio mInner;
        private static readonly Dictionary<uint, ulong> sObjectOfHandle = new Dictionary<uint, ulong>();
        private static readonly Dictionary<uint, bool> sPointHandles = new Dictionary<uint, bool>();
        private static Dictionary<string, bool> sOwnPointSounds;
        internal static long Lines;
        internal static long PointLines;
        internal static long PointsOwn;
        internal static long Stings;
        internal static long StingsUnowned;

        // Stings for their owner (ServerHostMain.PumpStings sends them from the bridge): { owner, name, sim or 0 }.
        internal static readonly List<string[]> PendingStings = new List<string[]>();

        // Who owns a Sim's sting (the claimed Sim the remote dialog is routed by), 0 = nobody. Set by ServerHostMain.
        internal static Converter<ulong, ulong> StingOwner;

        private AudioRecorder(IAudio inner)
        {
            mInner = inner;
        }

        internal static FieldInfo HolderField()
        {
            return typeof(Audio).GetField("gAudio", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = HolderField();
                return field != null && field.GetValue(null) is AudioRecorder;
            }
        }

        internal static string Install()
        {
            try
            {
                FieldInfo field = HolderField();
                if (field == null)
                {
                    return "audio=unavailable";
                }

                object current = field.GetValue(null);
                if (current is AudioRecorder)
                {
                    return "audio=on";
                }

                IAudio inner = current as IAudio;
                if (inner == null)
                {
                    return "audio=no-instance";
                }

                field.SetValue(null, new AudioRecorder(inner));
                return "audio=on";
            }
            catch (Exception ex)
            {
                return "audio=error:" + ex.GetType().Name;
            }
        }

        private static string U(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static void Record(ulong obj, string op)
        {
            try
            {
                if (SacsRecorder.RingEnabled && obj != 0uL)
                {
                    string line = "Au|" + U(obj) + "|" + op;
                    if (BoundaryState.Repeat(line, op[0] == 's' || op[0] == 'u' || op[0] == 'q'))
                    {
                        return; // a sound property or mute set again to the same value
                    }

                    Lines++;
                    SacsRecorder.RingLine(line);
                    // For a late joiner (BoundaryState): sounds still playing (looping or flagged), until stopped.
                    char kind = op[0];
                    string prefix = "Au|" + U(obj) + "|";
                    if ((kind == 'o' && op.EndsWith("|1")) || kind == 'w' || kind == 'a')
                    {
                        BoundaryState.Remember(line, 4);
                    }
                    else if (kind == 'y')
                    {
                        BoundaryState.ForgetKey(prefix + "a|" + op.Substring(2));
                    }
                    else if (kind == 'x')
                    {
                        string handle = op.Substring(2);
                        BoundaryState.ForgetKey(prefix + "o|" + handle);
                        BoundaryState.ForgetKey(prefix + "w|" + handle);
                    }
                    else if (kind == 'z')
                    {
                        BoundaryState.Forget(prefix + "o|");
                        BoundaryState.Forget(prefix + "w|");
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static uint Started(ObjectGuid obj, uint handle, string op)
        {
            if (handle != 0u)
            {
                if (sObjectOfHandle.Count > 4096)
                {
                    sObjectOfHandle.Clear();
                }

                sObjectOfHandle[handle] = obj.Value;
                Record(obj.Value, op);
            }

            return handle;
        }

        private static bool IsSting(string name)
        {
            return name != null && name.StartsWith("sting_", StringComparison.OrdinalIgnoreCase);
        }

        // A sting on a Sim (onSim), or played on a Sim's own task: queued for the Sim's player. True when queued.
        private static bool Sting(ulong sim, string name, bool onSim)
        {
            try
            {
                if (!SacsRecorder.RingEnabled || sim == 0uL || StingOwner == null)
                {
                    return false;
                }

                ulong owner = StingOwner(sim);
                if (owner == 0uL)
                {
                    StingsUnowned++;
                    return false;
                }

                if (PendingStings.Count < 64)
                {
                    PendingStings.Add(new string[] { U(owner), name, onSim ? U(sim) : "0" });
                    Stings++;
                }

                return true;
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
                return false;
            }
        }

        // The Sim whose task is running now (a bare sting's owner), or 0.
        private static ulong TaskSim()
        {
            try
            {
                ObjectGuid task = Simulator.CurrentTask;
                return task.IsValid && IsSim(task) ? task.Value : 0uL;
            }
            catch (Exception)
            {
                return 0uL;
            }
        }

        private static bool IsSim(ObjectGuid obj)
        {
            try
            {
                return Sims3.Gameplay.Abstracts.GameObject.GetObject<Sims3.Gameplay.Actors.Sim>(obj) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The point sounds each game makes itself: thunder, and a venue's ambience (VenueAmbienceManager's tuned lists).
        private static bool OwnPointSound(string name)
        {
            if (name.StartsWith("thunder_", StringComparison.OrdinalIgnoreCase))
            {
                return true; // weather thunder (SeasonsManager.DoThunder) stays each game's own
            }

            if (sOwnPointSounds == null)
            {
                sOwnPointSounds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Type venue = typeof(Sims3.Gameplay.Controllers.VenueAmbienceManager);
                    foreach (FieldInfo field in venue.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
                    {
                        string[] names = field.FieldType == typeof(string[]) ? field.GetValue(null) as string[] : null;
                        for (int i = 0; names != null && i < names.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(names[i]))
                            {
                                sOwnPointSounds[names[i]] = true;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    SwallowedErrors.Note();
                }
            }

            return sOwnPointSounds.ContainsKey(name);
        }

        private static void RecordPoint(string line)
        {
            try
            {
                if (SacsRecorder.RingEnabled)
                {
                    Lines++;
                    PointLines++;
                    SacsRecorder.RingLine(line); // never skipped as a repeat: two puddles at one spot are two sounds
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        public bool InitWorld()
        {
            return mInner.InitWorld();
        }

        public void ShutdownWorld()
        {
            mInner.ShutdownWorld();
        }

        public uint StartSound(string name)
        {
            uint handle = mInner.StartSound(name);
            if (IsSting(name))
            {
                Sting(TaskSim(), name, false);
            }

            return handle;
        }

        public uint StartSoundMP3(string name)
        {
            return mInner.StartSoundMP3(name);
        }

        public uint CreateSound(string name)
        {
            return mInner.CreateSound(name);
        }

        public bool StartSoundById(uint handle)
        {
            return mInner.StartSoundById(handle);
        }

        public bool StartSoundById(uint handle, Delegate finishedCallback)
        {
            return mInner.StartSoundById(handle, finishedCallback);
        }

        public uint StartSound(string name, Delegate finishedCallback)
        {
            uint handle = mInner.StartSound(name, finishedCallback);
            if (IsSting(name))
            {
                Sting(TaskSim(), name, false);
            }

            return handle;
        }

        public uint StartSound(string name, Vector3 position)
        {
            uint handle = mInner.StartSound(name, position);
            try
            {
                if (handle != 0u && SacsRecorder.RingEnabled && !string.IsNullOrEmpty(name))
                {
                    if (OwnPointSound(name))
                    {
                        PointsOwn++;
                    }
                    else
                    {
                        if (sPointHandles.Count > 4096)
                        {
                            sPointHandles.Clear();
                        }

                        sPointHandles[handle] = true;
                        CultureInfo c = CultureInfo.InvariantCulture;
                        RecordPoint("Au|0|t|" + handle.ToString(c) + "|" + position.x.ToString("R", c) + "|" + position.y.ToString("R", c) + "|"
                            + position.z.ToString("R", c) + "|" + BridgeCodec.EncodeText(name));
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return handle;
        }

        public bool StopSound(uint handle)
        {
            ulong obj;
            if (sObjectOfHandle.TryGetValue(handle, out obj))
            {
                sObjectOfHandle.Remove(handle);
                Record(obj, "x|" + handle.ToString(CultureInfo.InvariantCulture));
            }
            else if (sPointHandles.Remove(handle))
            {
                RecordPoint("Au|0|x|" + handle.ToString(CultureInfo.InvariantCulture));
            }

            return mInner.StopSound(handle);
        }

        public bool SetSoundProperty(uint handle, uint key, float val)
        {
            ulong obj;
            if (sObjectOfHandle.TryGetValue(handle, out obj))
            {
                Record(obj, "s|" + handle.ToString(CultureInfo.InvariantCulture) + "|" + key.ToString(CultureInfo.InvariantCulture) + "|"
                    + val.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (sPointHandles.ContainsKey(handle))
            {
                RecordPoint("Au|0|s|" + handle.ToString(CultureInfo.InvariantCulture) + "|" + key.ToString(CultureInfo.InvariantCulture) + "|"
                    + val.ToString("R", CultureInfo.InvariantCulture));
            }

            return mInner.SetSoundProperty(handle, key, val);
        }

        public void MuteAllObjectSounds(ObjectGuid objectHandle, bool bMute)
        {
            mInner.MuteAllObjectSounds(objectHandle, bMute);
            Record(objectHandle.Value, "u|" + (bMute ? "1" : "0"));
        }

        public uint StartObjectSound(ObjectGuid objectHandle, string name, bool loop)
        {
            uint handle = mInner.StartObjectSound(objectHandle, name, loop);
            if (handle != 0u && IsSting(name) && IsSim(objectHandle))
            {
                // The Sim's player's sting, not every client's that streams the Sim; with no claimed owner it stays on
                // the server (StingsUnowned), never an Au o line.
                Sting(objectHandle.Value, name, true);
                return handle;
            }

            return Started(objectHandle, handle, "o|" + handle.ToString(CultureInfo.InvariantCulture) + "|" + BridgeCodec.EncodeText(name ?? string.Empty) + "|" + (loop ? "1" : "0"));
        }

        public uint StartObjectSound(ObjectGuid objectHandle, string name, Delegate finishedCallback)
        {
            uint handle = mInner.StartObjectSound(objectHandle, name, finishedCallback);
            return Started(objectHandle, handle, "o|" + handle.ToString(CultureInfo.InvariantCulture) + "|" + BridgeCodec.EncodeText(name ?? string.Empty) + "|0");
        }

        public uint StartObjectSoundWithFlags(ObjectGuid objectHandle, string name, uint flags)
        {
            uint handle = mInner.StartObjectSoundWithFlags(objectHandle, name, flags);
            return Started(objectHandle, handle, "w|" + handle.ToString(CultureInfo.InvariantCulture) + "|" + BridgeCodec.EncodeText(name ?? string.Empty) + "|"
                + flags.ToString(CultureInfo.InvariantCulture));
        }

        public uint StartObjectSoundWithFlags(ObjectGuid objectHandle, string name, uint flags, Delegate finishedCallback)
        {
            uint handle = mInner.StartObjectSoundWithFlags(objectHandle, name, flags, finishedCallback);
            return Started(objectHandle, handle, "w|" + handle.ToString(CultureInfo.InvariantCulture) + "|" + BridgeCodec.EncodeText(name ?? string.Empty) + "|"
                + flags.ToString(CultureInfo.InvariantCulture));
        }

        public void StopObjectSound(ObjectGuid objectHandle, uint soundHandle)
        {
            mInner.StopObjectSound(objectHandle, soundHandle);
            sObjectOfHandle.Remove(soundHandle);
            Record(objectHandle.Value, "x|" + soundHandle.ToString(CultureInfo.InvariantCulture));
        }

        public void StopAllObjectSound(ObjectGuid objectHandle)
        {
            mInner.StopAllObjectSound(objectHandle);
            Record(objectHandle.Value, "z");
        }

        public void SetEventModifiers(Delegate simModifier, Delegate skillModifier, Delegate qualityModifier, Delegate materialModifier)
        {
            mInner.SetEventModifiers(simModifier, skillModifier, qualityModifier, materialModifier);
        }

        public void PauseGroup(uint group, bool pause)
        {
            mInner.PauseGroup(group, pause);
        }

        public void StopGroup(uint group, bool hardStop)
        {
            mInner.StopGroup(group, hardStop);
        }

        public void EnableGroup(uint group, bool enable)
        {
            mInner.EnableGroup(group, enable);
        }

        public uint PlayObjectPutSound(ObjectGuid srcObjectHandle, ObjectGuid targetObjectHandle)
        {
            uint handle = mInner.PlayObjectPutSound(srcObjectHandle, targetObjectHandle);
            Record(srcObjectHandle.Value, "p|" + U(targetObjectHandle.Value));
            return handle;
        }

        public uint PlayObjectGetSound(ObjectGuid srcObjectHandle, ObjectGuid targetObjectHandle)
        {
            uint handle = mInner.PlayObjectGetSound(srcObjectHandle, targetObjectHandle);
            Record(srcObjectHandle.Value, "g|" + U(targetObjectHandle.Value));
            return handle;
        }

        public uint PlayObjectPutOnFloorSound(ObjectGuid srcObjectHandle)
        {
            uint handle = mInner.PlayObjectPutOnFloorSound(srcObjectHandle);
            Record(srcObjectHandle.Value, "P");
            return handle;
        }

        public uint PlayObjectGetFromFloorSound(ObjectGuid srcObjectHandle)
        {
            uint handle = mInner.PlayObjectGetFromFloorSound(srcObjectHandle);
            Record(srcObjectHandle.Value, "G");
            return handle;
        }

        // A SoundArray plays up to 8 sounds, each on its own object
        // (SoundSpec: object id and name): the DJ turntable, a band's jam, karaoke and the singer career's music. Recorded
        // under the first child's object (what the stream scopes and maps by), with every child:
        //   Au|object|a|handle|hexName|obj~hexName;obj~hexName...   StartSoundArray
        //   Au|object|A|handle|children                             UpdateSoundArray
        //   Au|object|y|handle                                      StopSoundArray
        //   Au|object|q|handle|gain                                 SetSoundArrayGain
        private static ulong FirstObject(SoundSpec[] children)
        {
            for (int i = 0; children != null && i < children.Length; i++)
            {
                if (children[i].ObjectId != 0uL)
                {
                    return children[i].ObjectId;
                }
            }

            return 0uL;
        }

        private static string Children(SoundSpec[] children)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; children != null && i < children.Length; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(';');
                }

                sb.Append(U(children[i].ObjectId)).Append('~').Append(BridgeCodec.EncodeText(children[i].Name ?? string.Empty));
            }

            return sb.Length == 0 ? "-" : sb.ToString();
        }

        public uint StartSoundArray(string soundName, SoundSpec[] children)
        {
            uint handle = mInner.StartSoundArray(soundName, children);
            ulong obj = FirstObject(children);
            if (handle != 0u && obj != 0uL)
            {
                Started(new ObjectGuid(obj), handle, "a|" + handle.ToString(CultureInfo.InvariantCulture) + "|"
                    + BridgeCodec.EncodeText(soundName ?? string.Empty) + "|" + Children(children));
            }

            return handle;
        }

        public bool UpdateSoundArray(uint instanceId, SoundSpec[] children)
        {
            ulong obj;
            if (sObjectOfHandle.TryGetValue(instanceId, out obj))
            {
                Record(obj, "A|" + instanceId.ToString(CultureInfo.InvariantCulture) + "|" + Children(children));
            }

            return mInner.UpdateSoundArray(instanceId, children);
        }

        public bool StopSoundArray(uint instanceId)
        {
            ulong obj;
            if (sObjectOfHandle.TryGetValue(instanceId, out obj))
            {
                sObjectOfHandle.Remove(instanceId);
                Record(obj, "y|" + instanceId.ToString(CultureInfo.InvariantCulture));
            }

            return mInner.StopSoundArray(instanceId);
        }

        public bool SetSoundArrayGain(uint instanceId, float val)
        {
            ulong obj;
            if (sObjectOfHandle.TryGetValue(instanceId, out obj))
            {
                Record(obj, "q|" + instanceId.ToString(CultureInfo.InvariantCulture) + "|" + val.ToString("R", CultureInfo.InvariantCulture));
            }

            return mInner.SetSoundArrayGain(instanceId, val);
        }
    }
}
