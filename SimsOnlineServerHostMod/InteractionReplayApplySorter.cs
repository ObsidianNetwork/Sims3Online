// C#2 ONLY -- dual-compiled into DedicatedServer.Tests.
namespace SimsOnlineServerHost
{
    public static class InteractionReplayApplySorter
    {
        public static bool IsSitTransitionDeferred(string interactionKey)
        {
            return SitTransitionEmitFilter.IsSitTransitionReplay(interactionKey);
        }

        public static bool IsBookChainPrecursor(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            return interactionKey.StartsWith("Sims3.Gameplay.Objects.ReadBookChooser+");
        }

        public static bool IsGetBookPrecursor(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            return interactionKey.StartsWith("Sims3.Gameplay.Objects.GetBook+")
                || interactionKey.StartsWith("Sims3.Gameplay.Objects.Bookshelf_ReadSomething+");
        }

        public static bool IsReadBookTerminalHead(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            if (!interactionKey.StartsWith("Sims3.Gameplay.Objects.ReadBook+"))
            {
                return false;
            }

            return !interactionKey.StartsWith("Sims3.Gameplay.Objects.ReadBookChooser+");
        }

        // server-side inventory books carry host-local ObjectGuid values that do not exist
        // on the client sandbox. Client-role apply falls back to actor.Inventory.Find<BookGeneral>().
        public static bool UsesInventoryBookTargetFallback(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            return interactionKey.StartsWith("Sims3.Gameplay.Objects.GetBook+")
                || IsReadBookTerminalHead(interactionKey);
        }

        public static int PartitionSitTransitionLast(string[] keysInOrder, string[] nonSitOut, string[] sitOut)
        {
            int nonSitCount = 0;
            int sitCount = 0;
            for (int i = 0; i < keysInOrder.Length; i++)
            {
                string key = keysInOrder[i];
                if (IsSitTransitionDeferred(key))
                {
                    if (sitOut != null && sitCount < sitOut.Length)
                    {
                        sitOut[sitCount] = key;
                    }
                    sitCount++;
                }
                else
                {
                    if (nonSitOut != null && nonSitCount < nonSitOut.Length)
                    {
                        nonSitOut[nonSitCount] = key;
                    }
                    nonSitCount++;
                }
            }

            return nonSitCount;
        }
    }
}