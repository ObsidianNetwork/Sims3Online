using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // "reuse if unchanged" (docs/sim145-followup-scopes.md, Part 1): what a joiner gets only from the save, as one
    // line the controller compares between the last save and a join (DedicatedServer JoinWorldReuse). The scope is the
    // households of the given Sims (the controller sends every Sim claimed during its run), their home lots and the lots
    // their Sims stand on, never the world lot; "-" when no household resolves. The parts: the objects on those lots (id
    // and type, plus the spot to 0.25 m when loose), the inventories of the households' Sims and of the lots' objects
    // (item ids), and the households' relationships (long-term state, liking in steps of 10). Each part is an FNV-1a 64
    // hash of its sorted entries, as scripts/join_fingerprint.py measured it live (2-3.3 ms a scan, 21 October).
    // HandleSaveGame takes one right after the save, in the same handler, so no simulation step runs between the two.
    internal static class JoinFingerprint
    {
        public static string Describe(IList<Sim> sims)
        {
            long started = DateTime.UtcNow.Ticks;
            try
            {
                List<Household> households = new List<Household>();
                for (int i = 0; i < sims.Count; i++)
                {
                    Household household = sims[i] != null ? sims[i].Household : null;
                    if (household != null && !households.Contains(household))
                    {
                        households.Add(household);
                    }
                }

                List<Lot> lots = new List<Lot>();
                foreach (Household household in households)
                {
                    AddLot(lots, household.LotHome);
                    foreach (SimDescription d in household.AllSimDescriptions)
                    {
                        if (d != null && d.CreatedSim != null)
                        {
                            AddLot(lots, d.CreatedSim.LotCurrent);
                        }
                    }
                }

                List<string> objects = Objects(lots);
                int items = 0;
                List<string> inventories = Inventories(households, lots, ref items);
                List<string> relationships = Relationships(households);
                CultureInfo c = CultureInfo.InvariantCulture;
                StringBuilder text = new StringBuilder("scope=").Append(Scope(households, lots));
                text.Append(";objects=").Append(objects.Count.ToString(c)).Append(':').Append(Hash(objects));
                text.Append(";inventories=").Append(items.ToString(c)).Append(':').Append(Hash(inventories));
                text.Append(";relationships=").Append(relationships.Count.ToString(c)).Append(':').Append(Hash(relationships));
                text.Append(";ms=").Append(((DateTime.UtcNow.Ticks - started) / 10000.0).ToString("F2", c));
                return text.ToString();
            }
            catch (Exception ex)
            {
                return "error=" + ex.GetType().Name + ":" + (ex.Message ?? string.Empty).Replace(';', ',');
            }
        }

        private static void AddLot(List<Lot> lots, Lot lot)
        {
            if (lot != null && !lot.IsWorldLot && !lots.Contains(lot))
            {
                lots.Add(lot);
            }
        }

        // "h:<household ids>|l:<lot ids>", each sorted, so the same households on the same lots read the same whichever
        // of their Sims are claimed.
        private static string Scope(List<Household> households, List<Lot> lots)
        {
            if (households.Count == 0)
            {
                return "-";
            }

            List<ulong> householdIds = new List<ulong>();
            foreach (Household household in households)
            {
                householdIds.Add(household.HouseholdId);
            }

            List<ulong> lotIds = new List<ulong>();
            foreach (Lot lot in lots)
            {
                lotIds.Add(lot.LotId);
            }

            householdIds.Sort();
            lotIds.Sort();
            return "h:" + SimScopeWire.Format(householdIds) + "|l:" + SimScopeWire.Format(lotIds);
        }

        private static string Hash(List<string> entries)
        {
            entries.Sort(StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            foreach (string entry in entries)
            {
                foreach (char ch in entry)
                {
                    h = (h ^ ch) * 1099511628211UL;
                }

                h = (h ^ 10UL) * 1099511628211UL;
            }

            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static string Step(float v)
        {
            return ((int)Math.Round(v * 4f)).ToString(CultureInfo.InvariantCulture);
        }

        private static List<string> Objects(List<Lot> lots)
        {
            List<string> entries = new List<string>();
            foreach (Lot lot in lots)
            {
                foreach (GameObject obj in lot.GetObjects<GameObject>())
                {
                    if (obj == null || obj is Sim)
                    {
                        continue;
                    }

                    string entry = obj.ObjectId.Value.ToString(CultureInfo.InvariantCulture) + ":" + obj.GetType().Name;
                    if (obj.Parent == null && !obj.InInventory)
                    {
                        Vector3 p = obj.Position;
                        entry += ":" + Step(p.x) + "," + Step(p.y) + "," + Step(p.z);
                    }

                    entries.Add(entry);
                }
            }

            return entries;
        }

        private static void AddInventory(List<string> entries, ulong owner, Inventory inventory, ref int items)
        {
            if (inventory == null)
            {
                return;
            }

            List<string> ids = new List<string>();
            foreach (IGameObject item in inventory.FindAllOfType(typeof(IGameObject)))
            {
                ids.Add(item.ObjectId.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (ids.Count == 0)
            {
                return;
            }

            ids.Sort(StringComparer.Ordinal);
            items += ids.Count;
            entries.Add(owner.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", ids.ToArray()));
        }

        private static List<string> Inventories(List<Household> households, List<Lot> lots, ref int items)
        {
            List<string> entries = new List<string>();
            foreach (Household household in households)
            {
                foreach (SimDescription d in household.AllSimDescriptions)
                {
                    if (d != null && d.CreatedSim != null)
                    {
                        AddInventory(entries, d.CreatedSim.ObjectId.Value, d.CreatedSim.Inventory, ref items);
                    }
                }
            }

            foreach (Lot lot in lots)
            {
                foreach (GameObject obj in lot.GetObjects<GameObject>())
                {
                    if (obj != null && !(obj is Sim))
                    {
                        AddInventory(entries, obj.ObjectId.Value, obj.Inventory, ref items);
                    }
                }
            }

            return entries;
        }

        private static List<string> Relationships(List<Household> households)
        {
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            List<string> entries = new List<string>();
            foreach (Household household in households)
            {
                foreach (SimDescription d in household.AllSimDescriptions)
                {
                    if (d == null)
                    {
                        continue;
                    }

                    foreach (Relationship r in Relationship.GetRelationships(d))
                    {
                        SimDescription other = r.GetOtherSimDescription(d);
                        if (other == null || r.LTR == null)
                        {
                            continue;
                        }

                        ulong a = Math.Min(d.SimDescriptionId, other.SimDescriptionId);
                        ulong b = Math.Max(d.SimDescriptionId, other.SimDescriptionId);
                        string pair = a.ToString(CultureInfo.InvariantCulture) + "-" + b.ToString(CultureInfo.InvariantCulture);
                        if (seen.ContainsKey(pair))
                        {
                            continue;
                        }

                        seen[pair] = true;
                        entries.Add(pair + ":" + r.LTR.CurrentLTR.ToString() + ":"
                            + ((int)Math.Floor(r.LTR.Liking / 10f)).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            return entries;
        }
    }
}
