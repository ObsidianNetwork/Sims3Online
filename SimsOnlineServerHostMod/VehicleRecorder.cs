using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // . Observe at the shared posture/action boundary; Start at RouteCtor. No vehicle
    // interaction is run on a client. Native steering bypasses IObjects setters, so Publish samples
    // the active vehicle's actual transform, not the last script SetPosition call.
    internal static class VehicleRecorder
    {
        private sealed class Ride
        {
            internal ulong Vehicle;
            internal uint Seat;
            internal string Phase;
        }

        private sealed class Moving
        {
            internal WeakReference Route;
            internal uint Handle;
            internal long LastTick = -1L;
            internal Vector3 LastPosition;
        }

        private static readonly Dictionary<ulong, Ride> sRides = new Dictionary<ulong, Ride>();
        private static readonly Dictionary<ulong, Moving> sVehicles = new Dictionary<ulong, Moving>();
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        internal static void Observe(Sim sim)
        {
            if (!SacsRecorder.RingEnabled || !ServerHostMain.RouteStreamOn || sim == null)
            {
                return;
            }

            try
            {
                SittingInVehicle posture = sim.Posture as SittingInVehicle;
                Vehicle vehicle = posture == null ? null : posture.Container as Vehicle;
                // GetInVehicleRouteAction is internal in the shipped gameplay assembly.
                RouteAction action = sim.RoutingComponent == null ? null : sim.RoutingComponent.GetCurrentRouteAction();
                bool entering = action != null
                    && action.GetType().FullName == "Sims3.Gameplay.Routing.GetInVehicleRouteAction";
                Ride previous;
                sRides.TryGetValue(sim.ObjectId.Value, out previous);
                if (vehicle == null && !entering && previous == null)
                {
                    return;
                }

                Ride next = new Ride();
                next.Vehicle = vehicle == null ? 0uL : vehicle.ObjectId.Value;
                next.Seat = posture == null ? 0u : (uint)posture.SeatSlot;
                next.Phase = vehicle != null ? "ride" : entering ? "enter" : "release";
                if (next.Phase == "release")
                {
                    next.Vehicle = previous.Vehicle;
                    next.Seat = previous.Seat;
                }

                if (previous != null && previous.Vehicle == next.Vehicle && previous.Seat == next.Seat
                    && previous.Phase == next.Phase)
                {
                    return;
                }

                string line = "Vh|" + sim.ObjectId.Value.ToString(C) + "|" + next.Vehicle.ToString(C)
                    + "|" + next.Seat.ToString(C) + "|" + next.Phase + "|" + SimClock.CurrentTicks.ToString(C);
                SacsRecorder.RingLine(line);
                if (next.Phase == "release")
                {
                    sRides.Remove(sim.ObjectId.Value);
                    BoundaryState.ForgetKey("Vh|" + sim.ObjectId.Value.ToString(C));
                }
                else
                {
                    sRides[sim.ObjectId.Value] = next;
                    BoundaryState.Remember(line, 2);
                    if (vehicle != null && !sVehicles.ContainsKey(next.Vehicle))
                    {
                        Moving moving = new Moving();
                        moving.LastPosition = vehicle.Position;
                        sVehicles[next.Vehicle] = moving;
                    }
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        internal static void Start(Sims3.SimIFace.Route route)
        {
            if (!SacsRecorder.RingEnabled || !ServerHostMain.RouteStreamOn)
            {
                return;
            }

            try
            {
                IScriptProxy follower = route.Follower;
                Vehicle vehicle = follower == null ? null : follower.ObjectId.ObjectFromId<Vehicle>();
                if (vehicle == null || !route.PlanResult.Succeeded())
                {
                    return;
                }

                ulong id = vehicle.ObjectId.Value;
                Moving moving;
                if (!sVehicles.TryGetValue(id, out moving))
                {
                    moving = new Moving();
                    sVehicles[id] = moving;
                }

                moving.Route = new WeakReference(route);
                moving.Handle = route.Handle;
            moving.LastPosition = vehicle.Position;
                Vector3 goal = route.PlanResult.mDestination;
                string line = "Vs|" + id.ToString(C) + "|"
                    + (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond).ToString(C)
                    + "|" + goal.x.ToString("R", C) + "|" + goal.y.ToString("R", C) + "|" + goal.z.ToString("R", C)
                    + "|" + ServerHostMain.RouteWaypointsForStream(route) + "|" + route.Handle.ToString(C);
                SacsRecorder.RingLine(line);
                BoundaryState.Remember(line, 2);
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Call from the server's simulator-update pump, after Observe for that update. One Vp per
        // vehicle per simulation tick, independent of how many passengers it has.
        internal static void Publish()
        {
            if (!SacsRecorder.RingEnabled || !ServerHostMain.RouteStreamOn)
            {
                return;
            }

            try
            {
                long tick = SimClock.CurrentTicks;
                List<ulong> gone = new List<ulong>();
                foreach (KeyValuePair<ulong, Moving> pair in sVehicles)
                {
                    Vehicle vehicle = new ObjectGuid(pair.Key).ObjectFromId<Vehicle>();
                    Moving moving = pair.Value;
                Vector3 p = vehicle == null ? moving.LastPosition : vehicle.Position;
                moving.LastPosition = p;
                    Sims3.SimIFace.Route route = moving.Route == null ? null : moving.Route.Target as Sims3.SimIFace.Route;
                    if (moving.Route != null && (vehicle == null || route == null || !route.IsActive))
                    {
                        SacsRecorder.RingLine("Ve|" + pair.Key.ToString(C) + "|" + tick.ToString(C)
                            + "|" + p.x.ToString("R", C) + "|" + p.y.ToString("R", C) + "|" + p.z.ToString("R", C)
                            + "|" + moving.Handle.ToString(C));
                        BoundaryState.ForgetKey("Vs|" + pair.Key.ToString(C));
                        moving.Route = null;
                    }

                    bool occupied = false;
                    foreach (Ride ride in sRides.Values)
                    {
                        if (ride.Vehicle == pair.Key)
                        {
                            occupied = true;
                            break;
                        }
                    }

                    if (vehicle == null || (moving.Route == null && !occupied))
                    {
                        gone.Add(pair.Key);
                        BoundaryState.ForgetKey("Vp|" + pair.Key.ToString(C));
                        continue;
                    }

                    if (moving.LastTick == tick)
                    {
                        continue;
                    }

                    moving.LastTick = tick;
                    Vector3 f = vehicle.ForwardVector;
                    string line = "Vp|" + pair.Key.ToString(C) + "|" + tick.ToString(C)
                        + "|" + p.x.ToString("R", C) + "|" + p.y.ToString("R", C) + "|" + p.z.ToString("R", C)
                        + "|" + f.x.ToString("R", C) + "|" + f.z.ToString("R", C);
                    SacsRecorder.RingLine(line);
                    BoundaryState.Remember(line, 2);
                }

                for (int i = 0; i < gone.Count; i++)
                {
                    sVehicles.Remove(gone[i]);
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        internal static void Reset()
        {
            foreach (ulong sim in sRides.Keys)
            {
                BoundaryState.ForgetKey("Vh|" + sim.ToString(C));
            }
            foreach (ulong vehicle in sVehicles.Keys)
            {
                BoundaryState.ForgetKey("Vs|" + vehicle.ToString(C));
                BoundaryState.ForgetKey("Vp|" + vehicle.ToString(C));
            }
            sRides.Clear();
            sVehicles.Clear();
        }

        internal static bool Concerns(ulong vehicle, Dictionary<ulong, bool> targets)
        {
            if (targets == null)
            {
                return true;
            }
            foreach (KeyValuePair<ulong, Ride> ride in sRides)
            {
                if (ride.Value.Vehicle == vehicle && targets.ContainsKey(ride.Key))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
