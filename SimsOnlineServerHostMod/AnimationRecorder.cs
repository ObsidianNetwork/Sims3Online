using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // The direct-animation boundary (internal documentation). Animation that is not a state machine (bone
    // moves, transform and time controllers, clips and poses played straight on an object, random clip controllers)
    // goes through Sims3.SimIFace.Animation, whose implementation is read once from AppDomain data ("Animation") into the
    // private static Animation.Instance, like Slots and SacsProxy. This decorator is swapped in there on the server and
    // writes every call that changes what an object looks like into the recorder ring:
    //   An|object|op|args...
    // ops: b|bone|x|y|z|interp (SetBonePosition), r|bone|scale (SetBoneTimeRotation), t (CreateTransformController),
    // m (CreateTimeController), p|track|inst|type|group|scale (PlayAnimation), s|track|inst|type|group (StartPose),
    // z (StopAllAnimation), f|0/1 (ForceAnimation), q|controller|track|blend|mask|keys|weights|repeats
    // (StartRandomClipController; keys "inst~type~group" joined by ';'), w|track|controller (StopRandomClipController),
    // d|0/1 (force-discontinuous on parenting), l|0/1 (large-animal eval node), g|0/1|0/1 (scuba / mermaid),
    // h|0/1 (look-ats hidden), a|track|inst|type|group|sx|sy|sz|ex|ey|ez (procedural motion).
    // The ring keeps the lines of streamed Sims and props (the skill and progress meters, a mixing bowl), so a client's
    // copies move the same bones and play the same clips. Queries pass straight through. It never throws into the game.
    internal sealed class AnimationRecorder : IAnimation
    {
        private readonly IAnimation mInner;
        internal static long Lines;

        private AnimationRecorder(IAnimation inner)
        {
            mInner = inner;
        }

        private static FieldInfo InstanceField()
        {
            return typeof(Animation).GetField("Instance", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = InstanceField();
                return field != null && field.GetValue(null) is AnimationRecorder;
            }
        }

        internal static string Install()
        {
            try
            {
                FieldInfo field = InstanceField();
                if (field == null)
                {
                    return "animation=unavailable";
                }

                object current = field.GetValue(null);
                if (current is AnimationRecorder)
                {
                    return "animation=on";
                }

                IAnimation inner = current as IAnimation;
                if (inner == null)
                {
                    return "animation=no-instance";
                }

                field.SetValue(null, new AnimationRecorder(inner));
                return "animation=on";
            }
            catch (Exception ex)
            {
                return "animation=error:" + ex.GetType().Name;
            }
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string U(ulong value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Key(ResourceKey key)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return key.InstanceId.ToString(c) + "|" + key.TypeId.ToString(c) + "|" + key.GroupId.ToString(c);
        }

        private static string Keys(ResourceKey[] keys)
        {
            StringBuilder sb = new StringBuilder();
            CultureInfo c = CultureInfo.InvariantCulture;
            for (int i = 0; keys != null && i < keys.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(keys[i].InstanceId.ToString(c)).Append('~').Append(keys[i].TypeId.ToString(c)).Append('~').Append(keys[i].GroupId.ToString(c));
            }

            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static string Floats(float[] values)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; values != null && i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(F(values[i]));
            }

            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private static void Record(ObjectGuid id, string op)
        {
            try
            {
                if (SacsRecorder.RingEnabled && id.IsValid)
                {
                    string line = "An|" + U(id.Value) + "|" + op;
                    char kind = op[0];
                    // Setters (bones, flags) called again with the same value change nothing (SetLookAtsHidden runs every
                    // frame); playing, posing and controllers are actions.
                    bool setter = kind == 'b' || kind == 'r' || kind == 'f' || kind == 'd' || kind == 'l' || kind == 'g' || kind == 'h';
                    if (BoundaryState.Repeat(line, setter))
                    {
                        return;
                    }

                    Lines++;
                    SacsRecorder.RingLine(line);
                    Remember(id.Value, op, line);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // For a late joiner (BoundaryState): the latest bone move per bone, pose / clip per track, random clip controller
        // per controller, the rest per op; a stop-all ends the clips, poses and controllers, a controller's stop ends it.
        private static void Remember(ulong obj, string op, string line)
        {
            char kind = op[0];
            string prefix = "An|" + U(obj) + "|";
            if (kind == 'z')
            {
                BoundaryState.Forget(prefix + "p|");
                BoundaryState.Forget(prefix + "s|");
                BoundaryState.Forget(prefix + "q|");
                return;
            }

            if (kind == 'w')
            {
                string[] p = op.Split('|');
                if (p.Length > 2)
                {
                    BoundaryState.ForgetKey(prefix + "q|" + p[2]);
                }

                return;
            }

            BoundaryState.Remember(line, kind == 'b' || kind == 'r' || kind == 's' || kind == 'p' || kind == 'q' ? 4 : 3);
        }

        public void SetLookAtsHidden(ObjectGuid id, bool bHidden)
        {
            mInner.SetLookAtsHidden(id, bHidden);
            Record(id, "h|" + Flag(bHidden));
        }

        public void SetBonePosition(ObjectGuid objId, uint boneId, Vector3 pos, float interpTime)
        {
            mInner.SetBonePosition(objId, boneId, pos, interpTime);
            Record(objId, "b|" + boneId.ToString(CultureInfo.InvariantCulture) + "|" + F(pos.x) + "|" + F(pos.y) + "|" + F(pos.z) + "|" + F(interpTime));
        }

        public void CreateTransformController(ObjectGuid id)
        {
            mInner.CreateTransformController(id);
            Record(id, "t");
        }

        public void SetBoneTimeRotation(ObjectGuid objId, uint boneId, float timeScale)
        {
            mInner.SetBoneTimeRotation(objId, boneId, timeScale);
            Record(objId, "r|" + boneId.ToString(CultureInfo.InvariantCulture) + "|" + F(timeScale));
        }

        public void CreateTimeController(ObjectGuid id)
        {
            mInner.CreateTimeController(id);
            Record(id, "m");
        }

        public void PlayAnimation(ObjectGuid id, uint track, ResourceKey clipKey, float timeScale, bool yield)
        {
            Record(id, "p|" + track.ToString(CultureInfo.InvariantCulture) + "|" + Key(clipKey) + "|" + F(timeScale));
            mInner.PlayAnimation(id, track, clipKey, timeScale, yield);
        }

        public void StartPose(ObjectGuid id, uint track, ResourceKey clipKey)
        {
            mInner.StartPose(id, track, clipKey);
            Record(id, "s|" + track.ToString(CultureInfo.InvariantCulture) + "|" + Key(clipKey));
        }

        public void StopAllAnimation(ObjectGuid id)
        {
            mInner.StopAllAnimation(id);
            Record(id, "z");
        }

        public void ForceAnimation(ObjectGuid id, bool force)
        {
            mInner.ForceAnimation(id, force);
            Record(id, "f|" + Flag(force));
        }

        public uint StartRandomClipController(ObjectGuid objectId, uint track, float blendTime, ResourceKey trackMaskKey, ResourceKey[] resourceKeys, float[] weights, float[] repeats)
        {
            uint controller = mInner.StartRandomClipController(objectId, track, blendTime, trackMaskKey, resourceKeys, weights, repeats);
            Record(objectId, "q|" + controller.ToString(CultureInfo.InvariantCulture) + "|" + track.ToString(CultureInfo.InvariantCulture) + "|" + F(blendTime)
                + "|" + Key(trackMaskKey).Replace('|', '~') + "|" + Keys(resourceKeys) + "|" + Floats(weights) + "|" + Floats(repeats));
            return controller;
        }

        public void StopRandomClipController(ObjectGuid objectId, uint track, uint controllerId)
        {
            mInner.StopRandomClipController(objectId, track, controllerId);
            Record(objectId, "w|" + track.ToString(CultureInfo.InvariantCulture) + "|" + controllerId.ToString(CultureInfo.InvariantCulture));
        }

        public bool ClipExists(ulong clipId, uint groupId)
        {
            return mInner.ClipExists(clipId, groupId);
        }

        public bool HasAnimation(ObjectGuid id, uint track)
        {
            return mInner.HasAnimation(id, track);
        }

        public ulong GetClipInstanceId(string clipName)
        {
            return mInner.GetClipInstanceId(clipName);
        }

        public ulong GetClipInstanceIdForAges(string clipName, uint ageHashX, uint ageHashY)
        {
            return mInner.GetClipInstanceIdForAges(clipName, ageHashX, ageHashY);
        }

        public void PlayProcedurallyMotionAccumulatedAnimation(ObjectGuid id, uint track, ResourceKey clipKey, Vector3 startPos, Vector3 endPos)
        {
            Record(id, "a|" + track.ToString(CultureInfo.InvariantCulture) + "|" + Key(clipKey) + "|" + F(startPos.x) + "|" + F(startPos.y) + "|" + F(startPos.z)
                + "|" + F(endPos.x) + "|" + F(endPos.y) + "|" + F(endPos.z));
            mInner.PlayProcedurallyMotionAccumulatedAnimation(id, track, clipKey, startPos, endPos);
        }

        public void SetPMAAnimationFinishedCallback(PMAAnimationFinishedCallback pmaAnimationFinishedCallback)
        {
            mInner.SetPMAAnimationFinishedCallback(pmaAnimationFinishedCallback);
        }

        public Vector3 MeasureControllerDistanceTravelled(ObjectGuid id, ResourceKey clipKey)
        {
            return mInner.MeasureControllerDistanceTravelled(id, clipKey);
        }

        public void EnableDisableForceDiscontinuousOnParenting(ObjectGuid id, bool bEnable)
        {
            mInner.EnableDisableForceDiscontinuousOnParenting(id, bEnable);
            Record(id, "d|" + Flag(bEnable));
        }

        public void EnableDisableLargeAnimalEvalNode(ObjectGuid id, bool bEnable)
        {
            mInner.EnableDisableLargeAnimalEvalNode(id, bEnable);
            Record(id, "l|" + Flag(bEnable));
        }

        public void EnableDisableScubaDivingMode(ObjectGuid id, bool bEnable, bool bMermaid)
        {
            mInner.EnableDisableScubaDivingMode(id, bEnable, bMermaid);
            Record(id, "g|" + Flag(bEnable) + "|" + Flag(bMermaid));
        }
    }
}
