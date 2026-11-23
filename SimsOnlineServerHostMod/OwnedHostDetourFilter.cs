// C#2 ONLY -- dual-compiled into DedicatedServer.Tests (shared with client mirror gating).
namespace SimsOnlineServerHost
{
    /// <summary>
    /// autonomous host-origin detours that wedge the client bridge and fight book/couch
    /// chains. Skip mirror replay for owned Sims (not player echo).
    /// </summary>
    public static class OwnedHostDetourFilter
    {
        public static bool IsOwnedHostDetourSkip(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.Objects.Electronics.Phone+BrowseTheWeb+"))
            {
                return true;
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.Objects.Electronics.Phone+StreamVideo+"))
            {
                return true;
            }

            return false;
        }
    }
}