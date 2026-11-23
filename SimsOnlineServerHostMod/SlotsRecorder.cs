using System;
using System.Globalization;
using System.Reflection;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // item 4 (docs/engine/props-stream.md): props in hands. Putting an object into a Sim's hand,
    // onto a table or back on a shelf is script (GameObject.ParentToSlot / UnParent, often from an
    // animation event handler), not a state-machine call, so the animation stream never carried it: a
    // client Sim kept the book after reading. Every attach and detach goes through Sims3.SimIFace.Slots,
    // whose implementation is read once from AppDomain data into the private static gSlots, like
    // SacsProxy. This decorator is swapped in there (no fork) and writes the attach / detach calls into
    // the SacsRecorder ring, where the presentation stream picks them up:
    //   T|obj|parent|slot|occupy|forceBindPose
    //   Tx|obj|parent|slot|occupy|forceBindPose|tx|ty|tz|ax|ay|az|angle
    //   B|obj|parent|bone|occupy|forceBindPose|tx|ty|tz|ax|ay|az|angle
    //   U|obj|parent    (the parent it left, read before detaching)
    // Only successful calls are recorded. It never throws into the game.
    // Carrying (CarrySystem: a book from the shelf into the hand) parents the prop natively from the carry
    // animation, without an AttachToSlot call; the replayed animation does the same on a client. Putting
    // it away is often script (UnParent), so the detach names the parent it left: a detach from a
    // streamed Sim is relevant even though no attach line was ever seen (cold-test-038).
    internal sealed class SlotsRecorder : ISlots
    {
        private readonly ISlots mInner;

        private SlotsRecorder(ISlots inner)
        {
            mInner = inner;
        }

        private static FieldInfo SlotsField()
        {
            Type slots = typeof(ISlots).Assembly.GetType("Sims3.SimIFace.Slots");
            return slots == null ? null : slots.GetField("gSlots", BindingFlags.NonPublic | BindingFlags.Static);
        }

        internal static bool Installed
        {
            get
            {
                FieldInfo field = SlotsField();
                return field != null && field.GetValue(null) is SlotsRecorder;
            }
        }

        internal static string Install()
        {
            FieldInfo field = SlotsField();
            if (field == null)
            {
                return "slots=unavailable";
            }

            object current = field.GetValue(null);
            if (current is SlotsRecorder)
            {
                return "slots=on";
            }

            ISlots inner = current as ISlots;
            if (inner == null)
            {
                return "slots=no-proxy";
            }

            field.SetValue(null, new SlotsRecorder(inner));
            return "slots=on";
        }

        private static string Id(ObjectGuid id)
        {
            return id.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private static void Record(string line)
        {
            try
            {
                if (MotionWatch.Active)
                {
                    MotionWatch.NoteCall(line); // scripts/motion_watch.py: a watched Sim parented to a seat, a bed
                }

                SacsRecorder.RingLine(line);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        public bool EnableSlot(ObjectGuid handle, uint slotNameHash)
        {
            return mInner.EnableSlot(handle, slotNameHash);
        }

        public bool DisableSlot(ObjectGuid handle, uint slotNameHash)
        {
            return mInner.DisableSlot(handle, slotNameHash);
        }

        public bool IsSlotEnabled(ObjectGuid handle, uint slotNameHash)
        {
            return mInner.IsSlotEnabled(handle, slotNameHash);
        }

        public Vector3 GetPositionOfSlot(ObjectGuid handle, uint slotNameHash, uint ageGenderSpecies, bool isExitSlot)
        {
            return mInner.GetPositionOfSlot(handle, slotNameHash, ageGenderSpecies, isExitSlot);
        }

        public Matrix44 GetMatrixOfSlot(ObjectGuid handle, uint slotNameHash)
        {
            return mInner.GetMatrixOfSlot(handle, slotNameHash);
        }

        public TransformParentingReturnCode AttachToSlot(ObjectGuid objectId, ObjectGuid parentId, uint slotNameHash, bool occupySlot, bool forceBindPose)
        {
            TransformParentingReturnCode code = mInner.AttachToSlot(objectId, parentId, slotNameHash, occupySlot, forceBindPose);
            if ((int)code == 0)
            {
                Record("T|" + Id(objectId) + "|" + Id(parentId) + "|" + slotNameHash.ToString(CultureInfo.InvariantCulture) + "|" + Flag(occupySlot) + "|" + Flag(forceBindPose));
            }

            return code;
        }

        public TransformParentingReturnCode AttachToSlot(ObjectGuid objectId, ObjectGuid parentId, uint slotNameHash, bool occupySlot, bool forceBindPose, ref Vector3 trans, ref Vector3 axis, float angle)
        {
            TransformParentingReturnCode code = mInner.AttachToSlot(objectId, parentId, slotNameHash, occupySlot, forceBindPose, ref trans, ref axis, angle);
            if ((int)code == 0)
            {
                Record("Tx|" + Id(objectId) + "|" + Id(parentId) + "|" + slotNameHash.ToString(CultureInfo.InvariantCulture) + "|" + Flag(occupySlot) + "|" + Flag(forceBindPose)
                    + "|" + F(trans.x) + "|" + F(trans.y) + "|" + F(trans.z) + "|" + F(axis.x) + "|" + F(axis.y) + "|" + F(axis.z) + "|" + F(angle));
            }

            return code;
        }

        public TransformParentingReturnCode AttachToBone(ObjectGuid objectId, ObjectGuid parentId, uint boneNameHash, bool occupySlot, bool forceBindPose, ref Vector3 trans, ref Vector3 axis, float angle)
        {
            TransformParentingReturnCode code = mInner.AttachToBone(objectId, parentId, boneNameHash, occupySlot, forceBindPose, ref trans, ref axis, angle);
            if ((int)code == 0)
            {
                Record("B|" + Id(objectId) + "|" + Id(parentId) + "|" + boneNameHash.ToString(CultureInfo.InvariantCulture) + "|" + Flag(occupySlot) + "|" + Flag(forceBindPose)
                    + "|" + F(trans.x) + "|" + F(trans.y) + "|" + F(trans.z) + "|" + F(axis.x) + "|" + F(axis.y) + "|" + F(axis.z) + "|" + F(angle));
            }

            return code;
        }

        public TransformParentingReturnCode CanAttachToSlot(ObjectGuid objectId, ObjectGuid parentId, uint slotNameHash, bool occupySlot, bool forceBindPose)
        {
            return mInner.CanAttachToSlot(objectId, parentId, slotNameHash, occupySlot, forceBindPose);
        }

        public TransformParentingReturnCode CanAttachToSlot(ObjectGuid objectId, ObjectGuid parentId, uint slotNameHash, bool occupySlot, bool forceBindPose, ref Vector3 trans, ref Vector3 axis, float angle)
        {
            return mInner.CanAttachToSlot(objectId, parentId, slotNameHash, occupySlot, forceBindPose, ref trans, ref axis, angle);
        }

        public bool DetachFromSlot(ObjectGuid objectId)
        {
            ObjectGuid parent = ObjectGuid.InvalidObjectGuid;
            try
            {
                parent = mInner.GetParentId(objectId);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            bool detached = mInner.DetachFromSlot(objectId);
            if (detached)
            {
                Record("U|" + Id(objectId) + "|" + Id(parent));
            }

            return detached;
        }

        public ObjectGuid GetParentId(ObjectGuid handle)
        {
            return mInner.GetParentId(handle);
        }

        public ObjectGuid GetContainedObject(ObjectGuid handle, uint slotNameHash)
        {
            return mInner.GetContainedObject(handle, slotNameHash);
        }

        public Slot GetParentSlot(ObjectGuid handle, ref bool connectToBone)
        {
            return mInner.GetParentSlot(handle, ref connectToBone);
        }

        public ulong[] GetChildren(ObjectGuid handle, uint slotNameHash)
        {
            return mInner.GetChildren(handle, slotNameHash);
        }

        public uint[] GetContainmentSlotNames(ObjectGuid scriptHandle)
        {
            return mInner.GetContainmentSlotNames(scriptHandle);
        }

        public uint[] GetRoutingSlotNames(ObjectGuid scriptHandle)
        {
            return mInner.GetRoutingSlotNames(scriptHandle);
        }
    }
}
