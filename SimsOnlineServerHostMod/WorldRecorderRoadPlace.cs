using System;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    internal sealed partial class WorldRecorder
    {
        // The IWorld seam uses packed floats (World's managed facade constructs Vector3/Quaternion arrays).
        public bool FindPlaceOnRoadOffScreen(IScriptProxy scriptHandle, Vector3 pos, FindPlaceOnRoadOption option,
            float distance, ref float[] posArray, ref float[] orientArray)
        {
            if (!RustRoadPlace.Enabled)
            {
                RustRoadPlace.Count("off");
                return mInner.FindPlaceOnRoadOffScreen(scriptHandle, pos, option, distance, ref posArray, ref orientArray);
            }
            try
            {
                ulong obj = scriptHandle == null ? 0uL : scriptHandle.ObjectId.Value;
                // A positive default footprint for non-null proxies, including UFOs. Null has no fit check.
                int radius = obj == 0uL ? 0 : 50;
                // STOPGAP(boundary-recording): road-place-view-none managed CameraController exposes no frustum planes (OPEN-3); replace with verified native server camera frustum (RVA 0x3ced40 / 0x28d9e0)
                RustRoadPlace.Answer answer = RustRoadPlace.RequestAndWait(obj, pos.x, pos.y, pos.z, (int)option,
                    distance, radius, "none", Tunables.Get("road.rust.wait.seconds", 0.75), RouteRecorder.YieldEnv.Instance);
                if (answer != null)
                {
                    if (!answer.Found) return false;
                    posArray = new float[] { answer.X, answer.Y, answer.Z };
                    orientArray = new float[] { answer.Qx, answer.Qy, answer.Qz, answer.Qw };
                    return true;
                }
            }
            catch (Exception e)
            {
                if (RustRoutePlan.MustRethrow(e)) throw;
                RustRoadPlace.Count("fallback.noDaemon");
                SwallowedErrors.Note();
            }
            return mInner.FindPlaceOnRoadOffScreen(scriptHandle, pos, option, distance, ref posArray, ref orientArray);
        }
    }
}
