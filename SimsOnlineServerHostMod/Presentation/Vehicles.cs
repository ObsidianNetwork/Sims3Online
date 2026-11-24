using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;
using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // display only. Never PutInDriver/PutInPassenger, DoRoute, or construct a
    // SittingInVehicle posture: those execute gameplay. The server supplies the seat and poses.
    internal static class Vehicles
    {
        private sealed class Attachment
        {
            internal ObjectGuid Sim;
            internal ObjectGuid Vehicle;
            internal uint Seat;
        }

        private static readonly VehicleTravel sTravel = new VehicleTravel();
        private static readonly Dictionary<ulong, Attachment> sAttached = new Dictionary<ulong, Attachment>();
        private static readonly Dictionary<ulong, VehicleTravel.Pose> sApplied = new Dictionary<ulong, VehicleTravel.Pose>();
        private static long sErrors;

        internal static bool HoldsPosition(ulong localSim)
        {
            foreach (VehicleTravel.Rider rider in sTravel.Riders)
            {
                if (rider.Phase != "release" && PresentationApi.ReplayObject(rider.Sim).Value == localSim)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool Replay(string[] fields)
        {
            if (!sTravel.Apply(fields))
            {
                return false;
            }

            if (fields[1] == "Vh")
            {
                ulong localSim = PresentationApi.ReplayObject(ulong.Parse(fields[2], CultureInfo.InvariantCulture)).Value;
                Walks.ForgetMirror(localSim);
                if (sTravel.Holds(ulong.Parse(fields[2], CultureInfo.InvariantCulture)))
                {
                    Walks.TakeVehicleOwnership(localSim);
                }
            }
            Tick();
            if (fields[1] == "Vh" && fields[5] == "release")
            {
                Walks.ReleaseVehicleOwnership(PresentationApi.ReplayObject(ulong.Parse(fields[2], CultureInfo.InvariantCulture)).Value);
            }
            return true;
        }

        private static void Detach(Attachment attachment)
        {
            if (Slots.GetParentId(attachment.Sim) == attachment.Vehicle)
            {
                Slots.DetachFromSlot(attachment.Sim);
            }
        }

        internal static void Tick()
        {
            try
            {
                // Move the parent before attaching its passengers; late-join Vh may precede Vp.
                foreach (KeyValuePair<ulong, VehicleTravel.Pose> pair in sTravel.Poses)
                {
                    GameObject vehicle = PresentationApi.ReplayObject(pair.Key).ObjectFromId<GameObject>();
                    VehicleTravel.Pose applied;
                    if (vehicle == null || (sApplied.TryGetValue(pair.Key, out applied) && ReferenceEquals(applied, pair.Value)))
                    {
                        continue;
                    }

                    VehicleTravel.Pose p = pair.Value;
                    vehicle.SetPosition(new Vector3(p.X, p.Y, p.Z));
                    if (p.ForwardX * p.ForwardX + p.ForwardZ * p.ForwardZ > 0f)
                    {
                        vehicle.SetForward(new Vector3(p.ForwardX, 0f, p.ForwardZ));
                    }

                    sApplied[pair.Key] = p;
                }

                foreach (VehicleTravel.Rider rider in sTravel.Riders)
                {
                    Attachment old;
                    sAttached.TryGetValue(rider.Sim, out old);
                    ObjectGuid localSim = PresentationApi.ReplayObject(rider.Sim);
                    if (rider.Phase != "release" && localSim.IsValid)
                    {
                        PresentationApi.MarkStreamPositioned(localSim.Value);
                    }

                    ObjectGuid localVehicle = PresentationApi.ReplayObject(rider.Vehicle);
                    if (old != null && (rider.Phase != "ride" || old.Sim != localSim
                        || old.Vehicle != localVehicle || old.Seat != rider.Seat))
                    {
                        Detach(old);
                        sAttached.Remove(rider.Sim);
                        old = null;
                    }

                    if (rider.Phase != "ride" || !localSim.IsValid || !localVehicle.IsValid)
                    {
                        continue;
                    }

                    Sim sim = localSim.ObjectFromId<Sim>();
                    if (sim == null || localVehicle.ObjectFromId<GameObject>() == null)
                    {
                        continue;
                    }

                    // A pre-existing streamed slot attachment is also ours to release, but a
                    // different parent is not: leave it for the authoritative U/T boundary lines.
                    ObjectGuid parent = Slots.GetParentId(localSim);
                    if (parent.IsValid && parent != localVehicle)
                    {
                        continue;
                    }

                    if (parent != localVehicle
                        && (int)Slots.AttachToSlot(localSim, localVehicle, rider.Seat, true, false) != 0)
                    {
                        continue;
                    }

                    Attachment next = new Attachment();
                    next.Sim = localSim;
                    next.Vehicle = localVehicle;
                    next.Seat = rider.Seat;
                    sAttached[rider.Sim] = next;
                }
            }
            catch (Exception)
            {
                sErrors++;
            }
        }

        internal static void Reset()
        {
            foreach (Attachment attachment in sAttached.Values)
            {
                try
                {
                    Detach(attachment);
                }
                catch (Exception)
                {
                    sErrors++;
                }
            }

            sAttached.Clear();
            sApplied.Clear();
            sTravel.Clear();
        }

        internal static string Report()
        {
            return "attached:" + sAttached.Count.ToString(CultureInfo.InvariantCulture)
                + "/errors:" + sErrors.ToString(CultureInfo.InvariantCulture);
        }
    }
}
