// C#2 ONLY: source-linked into the controller tests; no engine types.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // authoritative occupancy and transforms. A vehicle route ending is NOT a passenger
    // dismount: only the server's posture/action observation releases the passenger's position hold.
    public sealed class VehicleTravel
    {
        public sealed class Rider
        {
            public ulong Sim;
            public ulong Vehicle;
            public uint Seat;
            public string Phase;
            public long Tick;
        }

        public sealed class Pose
        {
            public long Tick;
            public float X, Y, Z, ForwardX, ForwardZ;
        }

        private readonly Dictionary<ulong, Rider> mRiders = new Dictionary<ulong, Rider>();
        private readonly Dictionary<ulong, Pose> mPoses = new Dictionary<ulong, Pose>();
        private readonly Dictionary<ulong, uint> mRoutes = new Dictionary<ulong, uint>();

        public IEnumerable<Rider> Riders { get { return mRiders.Values; } }
        public IEnumerable<KeyValuePair<ulong, Pose>> Poses { get { return mPoses; } }

        public bool Holds(ulong sim)
        {
            Rider rider;
            return mRiders.TryGetValue(sim, out rider) && rider.Phase != "release";
        }

        public bool TryRider(ulong sim, out Rider rider)
        {
            return mRiders.TryGetValue(sim, out rider);
        }

        public bool HasRoute(ulong vehicle)
        {
            return mRoutes.ContainsKey(vehicle);
        }

        public void Clear()
        {
            mRiders.Clear();
            mPoses.Clear();
            mRoutes.Clear();
        }

        private static bool U(string s, out ulong n)
        {
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }

        private static bool L(string s, out long n)
        {
            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }

        private static bool F(string s, out float n)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                && !float.IsNaN(n) && !float.IsInfinity(n);
        }

        // seq|Vh|sim|vehicle|seat|enter/ride/release|tick
        // seq|Vp|vehicle|tick|x|y|z|fx|fz
        // seq|Vs|vehicle|startMs|goalX|goalY|goalZ|waypoints|route
        // seq|Ve|vehicle|tick|x|y|z|route
        public bool Apply(string[] f)
        {
            if (f == null || f.Length < 3)
            {
                return false;
            }

            ulong id;
            if (!U(f[2], out id) || id == 0uL)
            {
                return false;
            }

            if (f[1] == "Vh")
            {
                ulong vehicle, seat;
                long tick;
                if (f.Length != 7 || !U(f[3], out vehicle) || !U(f[4], out seat) || seat > uint.MaxValue
                    || !L(f[6], out tick) || (f[5] != "enter" && f[5] != "ride" && f[5] != "release")
                    || (f[5] == "ride" && vehicle == 0uL))
                {
                    return false;
                }

                Rider old;
                if (mRiders.TryGetValue(id, out old) && (tick < old.Tick
                    || (f[5] == "release" && old.Vehicle != vehicle)))
                {
                    return false;
                }

                Rider rider = new Rider();
                rider.Sim = id;
                rider.Vehicle = vehicle;
                rider.Seat = (uint)seat;
                rider.Phase = f[5];
                rider.Tick = tick;
                mRiders[id] = rider;
                return true;
            }

            if (f[1] == "Vp")
            {
                Pose pose = new Pose();
                Pose old;
                if (f.Length != 9 || !L(f[3], out pose.Tick)
                    || !F(f[4], out pose.X) || !F(f[5], out pose.Y) || !F(f[6], out pose.Z)
                    || !F(f[7], out pose.ForwardX) || !F(f[8], out pose.ForwardZ)
                    || (mPoses.TryGetValue(id, out old) && pose.Tick <= old.Tick))
                {
                    return false;
                }

                mPoses[id] = pose;
                return true;
            }

            if (f[1] == "Vs" || f[1] == "Ve")
            {
                ulong route;
                int index = f[1] == "Vs" ? 8 : 7;
                if (f.Length != index + 1 || !U(f[index], out route) || route > uint.MaxValue)
                {
                    return false;
                }

                if (f[1] == "Vs")
                {
                    mRoutes[id] = (uint)route;
                    return true;
                }

                uint current;
                if (mRoutes.TryGetValue(id, out current) && current != (uint)route)
                {
                    return false;
                }

                Pose end = new Pose();
                if (!L(f[3], out end.Tick) || !F(f[4], out end.X) || !F(f[5], out end.Y) || !F(f[6], out end.Z))
                {
                    return false;
                }

                Pose previous;
                bool hasPrevious = mPoses.TryGetValue(id, out previous);
                if (!hasPrevious || end.Tick >= previous.Tick)
                {
                    if (hasPrevious)
                    {
                        end.ForwardX = previous.ForwardX;
                        end.ForwardZ = previous.ForwardZ;
                    }

                    mPoses[id] = end;
                }

                mRoutes.Remove(id);
                return true;
            }

            return false;
        }
    }
}
