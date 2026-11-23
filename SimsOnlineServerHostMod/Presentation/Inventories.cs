using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // Inventories on a client (the server side, ServerHostMain.PublishInventories, sends
    // Iv|owner|item,...; InventorySet): the copy of the owner gets the server's set. Only the membership changes: an item
    // comes out without going back into the scene (TryToRemove(item, false)), because the stream already shows and
    // parents what leaves an inventory (H and slot lines: a book taken from a shelf is carried, a phone taken out is
    // unhidden), and an item that left on the server is not dropped on the lot here. An item the server's owner holds
    // that the copy does not comes out of any other inventory it is in on this client and goes in with
    // TryToAddTemporary: TryToAdd destroys an item added to an NPC Sim's inventory (Inventory.AddInternal's purge,
    // Inventory.cs:1199), and on a client the other player's Sims can be NPCs. An item this client has no object for is
    // counted, not made, and one in use here (its own copy's interaction) stays until the next set. Never ForceAdd:
    // when the add is refused it places the item in the world (PlaceAtGoodLocation).
    // Items the server made at runtime have ids no client has, and the client's copy of the owner holds its own object
    // instead (Milton Tong's BookGeneral, 2026-10-21): taking that out left it in neither the inventory nor the scene.
    // Only an item some server set named comes out. The client's own item (never named) is paired with a missing server
    // item of the same type (InventorySet.Pair, the line's types) and becomes that item's alias (PresentationApi.MatchObject,
    // as the stream pairs placed furniture), or else stays where it is (ownKept).
    internal static class Inventories
    {
        private const int NamedCapacity = 50000;

        private static long sLines;
        private static long sRemoved;
        private static long sAdded;
        private static long sInUse;
        private static long sMissing;
        private static long sRefused;
        private static long sNoOwner;
        private static long sPaired;
        private static long sOwnKept;
        private static string sLastMissing = "-"; // owner type:item id's last five digits
        private static string sLastRemoved = "-"; // owner type:item type
        private static string sLastPaired = "-"; // owner type:item type

        // This client's object ids that a server set has named (its copy of a server item).
        private static readonly Dictionary<ulong, bool> sNamed = new Dictionary<ulong, bool>();

        internal static void Reset()
        {
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return sLines.ToString(c) + "/+" + sAdded.ToString(c) + "/-" + sRemoved.ToString(c) + "/inUse:" + sInUse.ToString(c)
                + "/missing:" + sMissing.ToString(c) + "/refused:" + sRefused.ToString(c) + "/noOwner:" + sNoOwner.ToString(c)
                + "/paired:" + sPaired.ToString(c) + "/ownKept:" + sOwnKept.ToString(c)
                + "/lastMissing:" + sLastMissing + "/lastRemoved:" + sLastRemoved + "/lastPaired:" + sLastPaired;
        }

        internal static bool Replay(string[] f)
        {
            ulong owner;
            List<ulong> serverItems;
            List<string> serverTypes;
            if (!InventorySet.Parse(f, out owner, out serverItems, out serverTypes))
            {
                return false;
            }

            GameObject copy = PresentationApi.ReplayObject(owner).ObjectFromId<GameObject>();
            Inventory inventory = copy == null ? null : copy.Inventory;
            if (inventory == null)
            {
                sNoOwner++;
                return false;
            }

            sLines++;
            try
            {
                Dictionary<ulong, IGameObject> objects = new Dictionary<ulong, IGameObject>();
                List<ulong> target = new List<ulong>();
                List<ulong> missing = new List<ulong>();
                List<string> missingTypes = new List<string>();
                for (int i = 0; i < serverItems.Count; i++)
                {
                    ObjectGuid local = PresentationApi.ReplayObject(serverItems[i]);
                    GameObject item = local.ObjectFromId<GameObject>();
                    if (item == null)
                    {
                        missing.Add(serverItems[i]);
                        missingTypes.Add(serverTypes.Count == serverItems.Count ? serverTypes[i] : "?");
                        continue;
                    }

                    objects[local.Value] = item;
                    target.Add(local.Value);
                }

                if (sNamed.Count > NamedCapacity)
                {
                    sNamed.Clear();
                }

                for (int i = 0; i < target.Count; i++)
                {
                    sNamed[target[i]] = true;
                }

                List<ulong> current = new List<ulong>();
                List<IGameObject> held = inventory.FindAll<IGameObject>(false);
                for (int i = 0; i < held.Count; i++)
                {
                    if (held[i] != null)
                    {
                        objects[held[i].ObjectId.Value] = held[i];
                        current.Add(held[i].ObjectId.Value);
                    }
                }

                List<ulong> diffOut = new List<ulong>();
                List<ulong> add = new List<ulong>();
                InventorySet.Diff(current, target, diffOut, add);

                // Out only what a server set named; this client's own items are paired by type or kept.
                List<ulong> remove = new List<ulong>();
                List<ulong> own = new List<ulong>();
                List<string> ownTypes = new List<string>();
                for (int i = 0; i < diffOut.Count; i++)
                {
                    if (sNamed.ContainsKey(diffOut[i]))
                    {
                        remove.Add(diffOut[i]);
                    }
                    else
                    {
                        own.Add(diffOut[i]);
                        ownTypes.Add(objects[diffOut[i]].GetType().Name);
                    }
                }

                List<ulong> pairedServer = new List<ulong>();
                List<ulong> pairedLocal = new List<ulong>();
                InventorySet.Pair(missing, missingTypes, own, ownTypes, pairedServer, pairedLocal);
                Dictionary<ulong, bool> paired = new Dictionary<ulong, bool>();
                for (int i = 0; i < pairedServer.Count; i++)
                {
                    if (PresentationApi.MatchObject(pairedServer[i], new ObjectGuid(pairedLocal[i])))
                    {
                        paired[pairedServer[i]] = true;
                        sNamed[pairedLocal[i]] = true;
                        sPaired++;
                        sLastPaired = copy.GetType().Name + ":" + objects[pairedLocal[i]].GetType().Name;
                    }
                }

                sOwnKept += own.Count - paired.Count;
                for (int i = 0; i < missing.Count; i++)
                {
                    if (!paired.ContainsKey(missing[i]))
                    {
                        sMissing++;
                        sLastMissing = copy.GetType().Name + ":" + (missing[i] % 100000uL).ToString(CultureInfo.InvariantCulture);
                    }
                }

                for (int i = 0; i < remove.Count; i++)
                {
                    if (inventory.TryToRemove(objects[remove[i]], false))
                    {
                        sRemoved++;
                        sLastRemoved = copy.GetType().Name + ":" + objects[remove[i]].GetType().Name;
                    }
                    else
                    {
                        sInUse++;
                    }
                }

                for (int i = 0; i < add.Count; i++)
                {
                    IGameObject item = objects[add[i]];
                    Inventory other = item.ItemComp == null ? null : item.ItemComp.InventoryParent;
                    if (other != null && !ReferenceEquals(other, inventory) && !other.TryToRemove(item, false))
                    {
                        sInUse++;
                        continue;
                    }

                    if (inventory.TryToAddTemporary(item))
                    {
                        sAdded++;
                    }
                    else
                    {
                        sRefused++;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
