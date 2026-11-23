using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // The effects boundary (internal documentation). Every visual effect the game's scripts make goes through
    // Sims3.SimIFace.VisualEffect.Create, which builds the engine's concrete class (ScriptCore.VisualEffect) from the
    // constructor the engine leaves in AppDomain data ("VisualEffectCtor", cached in VisualEffect.sCtorInfo). On the
    // server this recording subclass is put there instead: every effect from any producer (thought balloons, the social
    // + and -, skill glows, sleep Zzz, cooking steam, expansions, mods) is recorded with all its calls. Nothing is decided
    // per producer.
    //
    // Each effect keeps a short log of its calls (create, overrides, parent, start). Every ParentTo writes the whole log
    // to the stream under the new parent:
    //   Vx|parent|effect|op|args...      (one line per call, the log first, then each later call)
    // The ring filter keeps the lines whose parent is a streamed Sim or a prop, like every other line. So a client gets
    // an effect whole from the first parent it can see, and an effect that never touches a streamed object never leaves
    // the server. Ops: c|hexName, p|slot|tx|ty|tz, s|transition|notify, x|transition, o|pos fwd up, k|r|g|b,
    // m|model|set, t|hash|set|group, 2|h1|s1|g1|h2|s2|g2, n|thumb|set, h|hexText, l|scale, a|0/1, v|0/1, f|type|v;v,
    // i|type|v;v, b|0/1, g|target|slot|tx|ty|tz|bone, d (disposed, or collected without a dispose).
    // Installed on the server only: a client plays the server's effects, its own copies make none of these.
    [Persistable]
    internal sealed class VfxRecorder : ScriptCore.VisualEffect
    {
        private const int LogCapacity = 48;

        // Streamed effects still alive, by effect handle: a collected one gets a "d" (the engine's finalizer destroys
        // the effect, and a client's copy of a looping one would otherwise play on).
        private static readonly Dictionary<ulong, KeyValuePair<WeakReference, ulong>> sLive = new Dictionary<ulong, KeyValuePair<WeakReference, ulong>>();
        internal static long Created;
        internal static long Parented;
        internal static long Lines;
        internal static long Collected;

        [Persistable(false)]
        private List<string> mLog;
        [Persistable(false)]
        private ulong mParent;

        public VfxRecorder()
        {
        }

        public VfxRecorder(string effectName)
            : base(effectName)
        {
            Created++;
            Log("c|" + BridgeCodec.EncodeText(effectName ?? string.Empty));
        }

        private static FieldInfo CtorField()
        {
            return typeof(Sims3.SimIFace.VisualEffect).GetField("sCtorInfo", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = CtorField();
                ConstructorInfo ctor = field == null ? null : field.GetValue(null) as ConstructorInfo;
                return ctor != null && ctor.DeclaringType == typeof(VfxRecorder);
            }
        }

        internal static string Install()
        {
            try
            {
                if (Installed)
                {
                    return "vfx=on";
                }

                ConstructorInfo ctor = typeof(VfxRecorder).GetConstructor(new Type[] { typeof(string) });
                FieldInfo field = CtorField();
                if (ctor == null || field == null)
                {
                    return "vfx=unavailable";
                }

                AppDomain.CurrentDomain.SetData("VisualEffectCtor", ctor);
                field.SetValue(null, ctor);
                return "vfx=on";
            }
            catch (Exception ex)
            {
                return "vfx=error:" + ex.GetType().Name;
            }
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "installed:" + (Installed ? "1" : "0") + ",created:" + Created.ToString(c) + ",parented:" + Parented.ToString(c)
                + ",lines:" + Lines.ToString(c) + ",live:" + sLive.Count.ToString(c) + ",collected:" + Collected.ToString(c);
        }

        // Late join (BoundaryState): every live streamed effect's log under its parent, for the targets and props of interest.
        internal static List<string> LiveLines(Dictionary<ulong, bool> targets)
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<ulong, KeyValuePair<WeakReference, ulong>> pair in sLive)
            {
                VfxRecorder effect = pair.Value.Key.Target as VfxRecorder;
                ulong parent = pair.Value.Value;
                if (effect == null || effect.mLog == null || (targets != null && !targets.ContainsKey(parent) && !SacsRecorder.Props.ContainsKey(parent)))
                {
                    continue;
                }

                for (int i = 0; i < effect.mLog.Count; i++)
                {
                    lines.Add("Vx|" + U(parent) + "|" + U(pair.Key) + "|" + effect.mLog[i]);
                }
            }

            return lines;
        }

        // Every prop poll: streamed effects collected without a dispose.
        internal static void ForgetCollected()
        {
            if (sLive.Count == 0)
            {
                return;
            }

            List<ulong> gone = new List<ulong>();
            foreach (KeyValuePair<ulong, KeyValuePair<WeakReference, ulong>> pair in sLive)
            {
                if (!pair.Value.Key.IsAlive)
                {
                    gone.Add(pair.Key);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                ulong parent = sLive[gone[i]].Value;
                sLive.Remove(gone[i]);
                Collected++;
                Emit(parent, gone[i], "d");
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

        private static string Join(float[] values)
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

        private static string Join(int[] values)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; values != null && i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(';');
                }

                sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static ulong IdOf(IHasScriptProxy obj)
        {
            try
            {
                return obj == null || obj.Proxy == null ? 0uL : obj.Proxy.ObjectId.Value;
            }
            catch (Exception)
            {
                return 0uL;
            }
        }

        private static void Emit(ulong parent, ulong effect, string op)
        {
            try
            {
                Lines++;
                SacsRecorder.RingLine("Vx|" + U(parent) + "|" + U(effect) + "|" + op);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private void Log(string op)
        {
            try
            {
                if (mLog == null)
                {
                    mLog = new List<string>();
                }

                if (mLog.Count < LogCapacity)
                {
                    mLog.Add(op);
                }

                if (mParent != 0uL)
                {
                    Emit(mParent, ObjectId.Value, op);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // A new parent: the whole log under it (the client makes the effect if it has none yet, else only re-parents).
        private void OnParented(ulong parent, string parentOp)
        {
            try
            {
                if (parent == 0uL)
                {
                    return;
                }

                if (mLog == null)
                {
                    mLog = new List<string>();
                }

                mParent = parent;
                Parented++;
                ulong effect = ObjectId.Value;
                for (int i = 0; i < mLog.Count; i++)
                {
                    Emit(parent, effect, mLog[i]);
                }

                if (mLog.Count < LogCapacity)
                {
                    mLog.Add(parentOp);
                }

                Emit(parent, effect, parentOp);
                sLive[effect] = new KeyValuePair<WeakReference, ulong>(new WeakReference(this), parent);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        public override bool ParentTo(IHasScriptProxy objectWithSlot, Slot slotOrBoneName, ref Vector3 trans)
        {
            bool ok = base.ParentTo(objectWithSlot, slotOrBoneName, ref trans);
            if (ok)
            {
                OnParented(IdOf(objectWithSlot), "p|" + ((uint)slotOrBoneName).ToString(CultureInfo.InvariantCulture) + "|" + F(trans.x) + "|" + F(trans.y) + "|" + F(trans.z));
            }

            return ok;
        }

        public override bool Start(TransitionType transitionType, bool bNotifyOnFinish)
        {
            Log("s|" + ((int)transitionType).ToString(CultureInfo.InvariantCulture) + "|" + (bNotifyOnFinish ? "1" : "0"));
            return base.Start(transitionType, bNotifyOnFinish);
        }

        public override bool Stop(TransitionType transitionType)
        {
            Log("x|" + ((int)transitionType).ToString(CultureInfo.InvariantCulture));
            return base.Stop(transitionType);
        }

        public override void SetPosAndOrient(Vector3 pos, Vector3 fwd, Vector3 up)
        {
            Log("o|" + F(pos.x) + "|" + F(pos.y) + "|" + F(pos.z) + "|" + F(fwd.x) + "|" + F(fwd.y) + "|" + F(fwd.z) + "|" + F(up.x) + "|" + F(up.y) + "|" + F(up.z));
            base.SetPosAndOrient(pos, fwd, up);
        }

        public override bool SetEffectColorScale(Vector3 colorRgbScale)
        {
            Log("k|" + F(colorRgbScale.x) + "|" + F(colorRgbScale.y) + "|" + F(colorRgbScale.z));
            return base.SetEffectColorScale(colorRgbScale);
        }

        public override bool SetEffectColorScale(float rScale, float gScale, float bScale)
        {
            Log("k|" + F(rScale) + "|" + F(gScale) + "|" + F(bScale));
            return base.SetEffectColorScale(rScale, gScale, bScale);
        }

        public override bool SetEffectModelOverride(int model, int modelSet)
        {
            Log("m|" + model.ToString(CultureInfo.InvariantCulture) + "|" + modelSet.ToString(CultureInfo.InvariantCulture));
            return base.SetEffectModelOverride(model, modelSet);
        }

        public override bool SetEffectTextureOverride(ulong textureHash, int textureSet, uint groupId)
        {
            Log("t|" + U(textureHash) + "|" + textureSet.ToString(CultureInfo.InvariantCulture) + "|" + groupId.ToString(CultureInfo.InvariantCulture));
            return base.SetEffectTextureOverride(textureHash, textureSet, groupId);
        }

        public override bool SetEffectDoubleTextureOverride(ulong texture1Hash, int texture1Override, uint groupId1, ulong texture2Hash, int texture2Override, uint groupId2)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            Log("2|" + U(texture1Hash) + "|" + texture1Override.ToString(c) + "|" + groupId1.ToString(c) + "|" + U(texture2Hash) + "|" + texture2Override.ToString(c) + "|" + groupId2.ToString(c));
            return base.SetEffectDoubleTextureOverride(texture1Hash, texture1Override, groupId1, texture2Hash, texture2Override, groupId2);
        }

        public override bool SetEffectThumbnailTextureOverride(ThumbnailKey key, int textureOverride)
        {
            Log("n|" + ServerHostMain.EncodeIconForStream(key) + "|" + textureOverride.ToString(CultureInfo.InvariantCulture));
            return base.SetEffectThumbnailTextureOverride(key, textureOverride);
        }

        public override bool SetEffectHeadlineTextureOverride(string str)
        {
            Log("h|" + BridgeCodec.EncodeText(str ?? string.Empty));
            return base.SetEffectHeadlineTextureOverride(str);
        }

        public override bool SetEffectLifeScale(float scale)
        {
            Log("l|" + F(scale));
            return base.SetEffectLifeScale(scale);
        }

        public override void SetAutoDestroy(bool destroy)
        {
            Log("a|" + (destroy ? "1" : "0"));
            base.SetAutoDestroy(destroy);
            if (destroy)
            {
                sLive.Remove(ObjectId.Value); // the engine ends it; nothing to collect
            }
        }

        public override void SetNeverHide(bool neverHide)
        {
            Log("v|" + (neverHide ? "1" : "0"));
            base.SetNeverHide(neverHide);
        }

        public override bool SetFloatParams(int paramType, float[] vars)
        {
            Log("f|" + paramType.ToString(CultureInfo.InvariantCulture) + "|" + Join(vars));
            return base.SetFloatParams(paramType, vars);
        }

        public override bool SetIntParams(int paramType, int[] vars)
        {
            Log("i|" + paramType.ToString(CultureInfo.InvariantCulture) + "|" + Join(vars));
            return base.SetIntParams(paramType, vars);
        }

        public override void SetUseBoneParenting(bool connectToBone)
        {
            Log("b|" + (connectToBone ? "1" : "0"));
            base.SetUseBoneParenting(connectToBone);
        }

        public override bool SetTargetObjectAndSlotOrBone(IHasScriptProxy objectWithSlot, Slot slotOrBoneName, Vector3 trans, bool bIsBone)
        {
            Log("g|" + U(IdOf(objectWithSlot)) + "|" + ((uint)slotOrBoneName).ToString(CultureInfo.InvariantCulture) + "|" + F(trans.x) + "|" + F(trans.y) + "|" + F(trans.z)
                + "|" + (bIsBone ? "1" : "0"));
            return base.SetTargetObjectAndSlotOrBone(objectWithSlot, slotOrBoneName, trans, bIsBone);
        }

        public override void Dispose()
        {
            try
            {
                ulong effect = ObjectId.Value;
                if (mParent != 0uL)
                {
                    Emit(mParent, effect, "d");
                }

                sLive.Remove(effect);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            base.Dispose();
        }
    }
}
