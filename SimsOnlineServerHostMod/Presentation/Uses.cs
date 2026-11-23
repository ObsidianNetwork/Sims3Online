using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;
using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // Object use on a client (the server side, ServerHostMain.PublishUses, sends
    // Us|object|sim,...|area:sim,...; ObjectUse): the copy of the object gets the server's users, so the client's own
    // menu tests see it in use (GameObject.InUse, a part's CanBeUsedBy: Nap on a loveseat the other player sits on).
    // Only the lists change, and only through the mirror's own entries: a Sim goes into the copy's ActorsUsingMe list
    // directly, not through AddToUseList (virtual, with object-specific overrides: Computer, BunkBedBunk, ...), and a
    // part's Sim is set on PartData.mContainedSim directly, not through SetContainedSim (it runs the object's
    // OnPartAvailabilityChangedCallback). An entry the client's own game put there is never taken out, and a part that
    // holds the client's own Sim is left (busy). A Sim this client has no copy of is counted, not added. Reset takes the
    // mirror's entries out again, so a reloaded module starts from the copies' own lists. A part type that overrides
    // ContainedSim (ActivityTable) does not read the field and keeps its own state.
    internal static class Uses
    {
        private static long sLines;
        private static long sAdded;
        private static long sRemoved;
        private static long sPartsSet;
        private static long sPartsCleared;
        private static long sPartsBusy;
        private static long sNoObject;
        private static long sNoSim;
        private static long sNoPart;
        private static long sErrors;

        // This client's copies -> the Sims the mirror put in their use lists, and the parts it set (PartArea -> Sim).
        private static readonly Dictionary<ulong, List<ulong>> sMine = new Dictionary<ulong, List<ulong>>();
        private static readonly Dictionary<ulong, Dictionary<uint, ulong>> sMineParts = new Dictionary<ulong, Dictionary<uint, ulong>>();
        private static FieldInfo sContainedSim;
        private static bool sResolved;

        private static FieldInfo ContainedSimField()
        {
            if (!sResolved)
            {
                sResolved = true;
                sContainedSim = typeof(PartData).GetField("mContainedSim", BindingFlags.Instance | BindingFlags.NonPublic);
            }

            return sContainedSim;
        }

        internal static void Reset()
        {
            try
            {
                foreach (KeyValuePair<ulong, List<ulong>> mine in sMine)
                {
                    GameObject copy = new ObjectGuid(mine.Key).ObjectFromId<GameObject>();
                    List<Sim> list = copy == null ? null : copy.ActorsUsingMe;
                    for (int i = 0; list != null && i < mine.Value.Count; i++)
                    {
                        Sim sim = new ObjectGuid(mine.Value[i]).ObjectFromId<Sim>();
                        if (sim != null)
                        {
                            list.Remove(sim);
                        }
                    }
                }

                FieldInfo field = ContainedSimField();
                foreach (KeyValuePair<ulong, Dictionary<uint, ulong>> mine in sMineParts)
                {
                    GameObject copy = new ObjectGuid(mine.Key).ObjectFromId<GameObject>();
                    PartComponent parts = copy == null ? null : copy.PartComponent;
                    if (parts == null || parts.PartDataList == null || field == null)
                    {
                        continue;
                    }

                    foreach (KeyValuePair<PartArea, PartData> part in parts.PartDataList)
                    {
                        ulong simId;
                        Sim now = part.Value == null ? null : field.GetValue(part.Value) as Sim;
                        if (now != null && mine.Value.TryGetValue((uint)(int)part.Key, out simId) && now.ObjectId.Value == simId)
                        {
                            field.SetValue(part.Value, null);
                        }
                    }
                }
            }
            catch (Exception)
            {
                sErrors++;
            }

            sMine.Clear();
            sMineParts.Clear();
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            // No commas: the shell's report lists the modules' counters comma-separated (Module.Report).
            return sLines.ToString(c) + "/+" + sAdded.ToString(c) + "/-" + sRemoved.ToString(c) + "/partsSet:" + sPartsSet.ToString(c)
                + "/partsCleared:" + sPartsCleared.ToString(c) + "/busy:" + sPartsBusy.ToString(c) + "/noObject:" + sNoObject.ToString(c)
                + "/noSim:" + sNoSim.ToString(c) + "/noPart:" + sNoPart.ToString(c) + "/errors:" + sErrors.ToString(c)
                + "/mine:" + sMine.Count.ToString(c) + "/mineParts:" + sMineParts.Count.ToString(c);
        }

        private static Sim Local(ulong serverSim)
        {
            return PresentationApi.ReplayObject(serverSim).ObjectFromId<Sim>();
        }

        internal static bool Replay(string[] f)
        {
            ulong obj;
            List<ulong> users;
            List<uint> areas;
            List<ulong> partSims;
            if (!ObjectUse.Parse(f, out obj, out users, out areas, out partSims))
            {
                return false;
            }

            GameObject copy = PresentationApi.ReplayObject(obj).ObjectFromId<GameObject>();
            if (copy == null)
            {
                sNoObject++;
                return false;
            }

            sLines++;
            try
            {
                ulong copyId = copy.ObjectId.Value;
                ReplayUsers(copy, copyId, users);
                ReplayParts(copy, copyId, areas, partSims);
                return true;
            }
            catch (Exception)
            {
                sErrors++;
                return false;
            }
        }

        private static void ReplayUsers(GameObject copy, ulong copyId, List<ulong> users)
        {
            List<Sim> list = copy.ActorsUsingMe;
            if (list == null)
            {
                return;
            }

            Dictionary<ulong, Sim> sims = new Dictionary<ulong, Sim>();
            List<ulong> want = new List<ulong>();
            for (int i = 0; i < users.Count; i++)
            {
                Sim sim = Local(users[i]);
                if (sim == null)
                {
                    sNoSim++;
                    continue;
                }

                sims[sim.ObjectId.Value] = sim;
                want.Add(sim.ObjectId.Value);
            }

            List<ulong> current = new List<ulong>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null)
                {
                    sims[list[i].ObjectId.Value] = list[i];
                    current.Add(list[i].ObjectId.Value);
                }
            }

            List<ulong> mine;
            if (!sMine.TryGetValue(copyId, out mine))
            {
                mine = new List<ulong>();
            }

            List<ulong> add = new List<ulong>();
            List<ulong> remove = new List<ulong>();
            ObjectUse.Reconcile(current, mine, want, add, remove);
            for (int i = 0; i < remove.Count; i++)
            {
                list.Remove(sims[remove[i]]);
                sRemoved++;
            }

            for (int i = 0; i < add.Count; i++)
            {
                list.Add(sims[add[i]]);
                sAdded++;
            }

            // Still the mirror's: its earlier entries the server still lists and the list still holds, and the new ones.
            List<ulong> next = new List<ulong>(add);
            for (int i = 0; i < mine.Count; i++)
            {
                if (want.Contains(mine[i]) && current.Contains(mine[i]) && !next.Contains(mine[i]))
                {
                    next.Add(mine[i]);
                }
            }

            if (next.Count > 0)
            {
                sMine[copyId] = next;
            }
            else
            {
                sMine.Remove(copyId);
            }
        }

        private static void ReplayParts(GameObject copy, ulong copyId, List<uint> areas, List<ulong> partSims)
        {
            Dictionary<uint, Sim> want = new Dictionary<uint, Sim>();
            for (int i = 0; i < areas.Count; i++)
            {
                Sim sim = Local(partSims[i]);
                if (sim == null)
                {
                    sNoSim++;
                    continue;
                }

                want[areas[i]] = sim;
            }

            Dictionary<uint, ulong> mine;
            if (!sMineParts.TryGetValue(copyId, out mine))
            {
                mine = new Dictionary<uint, ulong>();
            }

            PartComponent parts = copy.PartComponent;
            FieldInfo field = ContainedSimField();
            if (parts == null || parts.PartDataList == null || field == null)
            {
                sNoPart += want.Count;
                sMineParts.Remove(copyId);
                return;
            }

            int matched = 0;
            foreach (KeyValuePair<PartArea, PartData> part in parts.PartDataList)
            {
                if (part.Value == null)
                {
                    continue;
                }

                uint area = (uint)(int)part.Key;
                Sim now = field.GetValue(part.Value) as Sim;
                ulong mineSim;
                bool isMine = mine.TryGetValue(area, out mineSim) && now != null && now.ObjectId.Value == mineSim;
                if (!isMine)
                {
                    // Not set by the mirror, or the client's own game changed it since.
                    mine.Remove(area);
                }

                Sim wanted;
                if (want.TryGetValue(area, out wanted))
                {
                    matched++;
                    if (ReferenceEquals(now, wanted))
                    {
                        continue;
                    }

                    if (now == null || isMine)
                    {
                        field.SetValue(part.Value, wanted);
                        mine[area] = wanted.ObjectId.Value;
                        sPartsSet++;
                    }
                    else
                    {
                        sPartsBusy++;
                    }
                }
                else if (isMine)
                {
                    field.SetValue(part.Value, null);
                    mine.Remove(area);
                    sPartsCleared++;
                }
            }

            sNoPart += want.Count - matched;
            if (mine.Count > 0)
            {
                sMineParts[copyId] = mine;
            }
            else
            {
                sMineParts.Remove(copyId);
            }
        }
    }
}
