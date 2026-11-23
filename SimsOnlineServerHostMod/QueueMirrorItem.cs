using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;
using Sims3.UI.CAS;
using Sims3.UI.Hud;

namespace SimsOnlineServerHost
{
    // queue mirror (docs/engine/input-forwarding.md): a client runs no interactions, so its HUD
    // queue would stay empty. The fork's HudModel serves these display stand-ins for the server's queue
    // of the active Sim (AuthorityHook.DisplayQueue): name, icon (the target's thumbnail on this client)
    // and whether it is running. Cancelling one from the HUD goes through HudModel.CancelInteraction,
    // which parks a cancel intent for the server.
    internal sealed class QueueMirrorItem : IInteractionInstance
    {
        private readonly ulong mId;
        private readonly ulong mGroupId;
        private readonly string mName;
        private readonly bool mRunning;
        private readonly ObjectGuid mTarget;
        private readonly Sim mActor;
        private readonly bool mNested;
        private readonly ThumbnailKey mIcon;
        private readonly bool mHasIcon;
        private readonly MirrorStage mProgressStage;
        private bool mHidden;
        private bool mUpdateCaption;
        private ITone mTone;
        private IStage mStage;

        // groupId: the server item's group (a continuation keeps its chain's group, and the HUD shows a run of items
        // sharing a group as one connected item); 0 from an older server: the item's own id.
        // nested: the server's HUD would pin this item to the posture (the Sim's posture satisfies its posture
        // preconditions: reading in a chair, sleeping in a bed), so the client's HUD draws them as one.
        // icon: the server item's icon (hasIcon false: an older server, the target's thumbnail on this client).
        // progress: the server item's progress bar (0..1; below 0: none).
        internal QueueMirrorItem(ulong id, ulong groupId, string name, bool running, ObjectGuid target, Sim actor, bool nested, ThumbnailKey icon, bool hasIcon, float progress)
        {
            mProgressStage = progress >= 0f ? new MirrorStage(progress) : null;
            mId = id;
            mGroupId = groupId == 0uL ? id : groupId;
            mName = name;
            mRunning = running;
            mTarget = target;
            mActor = actor;
            mNested = nested;
            mIcon = icon;
            mHasIcon = hasIcon;
        }

        internal bool Nested
        {
            get { return mNested; }
        }

        internal bool Running
        {
            get { return mRunning; }
        }

        public ulong Id
        {
            get { return mId; }
        }

        public ulong GroupId
        {
            get { return mGroupId; }
        }

        public ITone CurrentITone
        {
            get { return mTone; }
            set { mTone = value; }
        }

        public bool Cancelled
        {
            get { return false; }
        }

        public bool Autonomous
        {
            get { return false; }
        }

        public bool Hidden
        {
            get { return mHidden; }
            set { mHidden = value; }
        }

        public List<IStage> IStages
        {
            get
            {
                List<IStage> stages = new List<IStage>();
                if (mProgressStage != null)
                {
                    stages.Add(mProgressStage);
                }

                return stages;
            }
        }

        public IStage ActiveIStage
        {
            get { return mStage ?? mProgressStage; }
            set { mStage = value; }
        }

        // The server item's progress for the HUD's bar (sleep, homework): one stage, not selectable.
        private sealed class MirrorStage : IStage
        {
            private readonly float mPortion;

            internal MirrorStage(float portion)
            {
                mPortion = portion;
            }

            public bool VisibleProgress
            {
                get { return true; }
            }

            public bool Selectable
            {
                get { return false; }
            }

            public bool IsComplete(IInteractionInstance instance)
            {
                return false;
            }

            public float TotalTime()
            {
                return 1f;
            }

            public float PortionComplete(IInteractionInstance instance)
            {
                return mPortion;
            }

            public float TimeRemaining(IInteractionInstance instance)
            {
                return 0f;
            }

            public ResourceKey GetIconKey()
            {
                return ResourceKey.kInvalidResourceKey;
            }
        }

        // True: the HUD's item update calls RemoveInteractionByRef on an item whose StandardEntryCalled is
        // false and that has no active stage (InteractionQueueItem), which cancelled every queued stand-in.
        public bool StandardEntryCalled
        {
            get { return true; }
        }

        public object IInstanceActor
        {
            get { return mActor; }
        }

        public bool IsWorkingInCareerInteraction
        {
            get { return false; }
        }

        public bool IsSocialInteraction
        {
            get { return false; }
        }

        public string DefaultToneText
        {
            get { return string.Empty; }
        }

        public string DefaultToneDescription
        {
            get { return string.Empty; }
        }

        public ISimDescription TargetActorSimDescription
        {
            get { return null; }
        }

        public ObjectGuid InstanceActorGuid
        {
            get { return mActor != null ? mActor.ObjectId : ObjectGuid.InvalidObjectGuid; }
        }

        public bool UpdateCaption
        {
            get { return mUpdateCaption; }
            set { mUpdateCaption = value; }
        }

        // Null, as the engine's interactions without tones return: the HUD draws any non-null list (even an empty one) in
        // the tone style, a wide frame with the caption and a tone drop-down, so every mirrored item looked like a social.
        public List<InteractionToneDisplay> AvailableTonesForDisplay()
        {
            return null;
        }

        public string GetInteractionNameForUI()
        {
            return mName;
        }

        // The HUD also calls this from its own bookkeeping, not only on a player's cancel; the cancel button
        // goes through HudModel.CancelInteraction (fork), so this does nothing.
        public void RemoveInteractionByRef()
        {
        }

        public ThumbnailKey GetIconKey()
        {
            if (mHasIcon)
            {
                return mIcon;
            }

            GameObject target = mTarget.IsValid ? mTarget.ObjectFromId<GameObject>() : null;
            if (target == null)
            {
                return default(ThumbnailKey);
            }

            ThumbnailKey key = target.GetThumbnailKey();
            key.mSize = (ThumbnailSize)2;
            return key;
        }
    }
}
