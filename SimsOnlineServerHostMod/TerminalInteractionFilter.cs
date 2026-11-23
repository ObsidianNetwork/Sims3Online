// C#2 ONLY -- same dual-compile rules as RoutingInteractionFilter.cs (mod root, no engine types).
namespace SimsOnlineServerHost
{
    public static class TerminalInteractionFilter
    {
        // Engine-pushed interactions that may appear on the wire as telemetry but must never be
        // replayed on a client puppet. Fail-closed blacklist for replay only;
        // capture-forward uses its own allowlist (ClientCaptureFilter).
        private static readonly string[] NeverReplayDefinitionPrefixes = new string[]
        {
            "Sims3.Gameplay.ActorSystems.ReactionInteraction+"
        };

        private static readonly string[] TerminalDefinitionPrefixes = new string[]
        {
            "Sims3.Gameplay.Objects.ReadBook+",
            "Sims3.Gameplay.Objects.GetBook+",
            "Sims3.Gameplay.Objects.ReadBookChooser+",
            "Sims3.Gameplay.Objects.PutBookDown+",
            "Sims3.Gameplay.Objects.Bookshelf_ReadSomething+",
            "Sims3.Gameplay.InteractionsShared.Stand+",
            "Sims3.Gameplay.InteractionsShared.Sit+",
            "Sims3.Gameplay.Objects.Electronics.Phone+BrowseTheWeb+",
            "Sims3.Gameplay.Objects.Electronics.Phone+StreamVideo+",
            // social approach: autonomous SocializeWith picks FindBestSocial on the client.
            "Sims3.Gameplay.Actors.Sim+SocializeWith+"
        };

        // Parameterized social instances (no shared Singleton) replay as GoHere-to-target on the client.
        private static readonly string[] SocialRouteDefinitionPrefixes = new string[]
        {
            "Sims3.Gameplay.Socializing.SocialInteractionA+",
            "Sims3.Gameplay.Socializing.SocialInteractionB+"
        };

        public static bool IsNeverReplay(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < NeverReplayDefinitionPrefixes.Length; i++)
            {
                string prefix = NeverReplayDefinitionPrefixes[i];
                if (interactionKey == prefix + "Definition")
                {
                    return true;
                }

                if (interactionKey.Length > prefix.Length && interactionKey.StartsWith(prefix))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsTerminalReplay(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            if (IsNeverReplay(interactionKey))
            {
                return false;
            }

            if (RoutingInteractionFilter.IsRoutingInteraction(interactionKey))
            {
                return false;
            }

            if (interactionKey == "Terrain.GoHere" || interactionKey.Contains("Terrain+GoHere"))
            {
                return false;
            }

            for (int i = 0; i < TerminalDefinitionPrefixes.Length; i++)
            {
                string prefix = TerminalDefinitionPrefixes[i];
                if (interactionKey == prefix + "Definition")
                {
                    return true;
                }

                if (interactionKey.Length > prefix.Length && interactionKey.StartsWith(prefix))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsSocialApproachReplay(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            if (interactionKey.StartsWith("Sims3.Gameplay.Actors.Sim+SocializeWith+"))
            {
                return true;
            }

            return MatchesPrefixList(interactionKey, SocialRouteDefinitionPrefixes);
        }

        public static bool IsRouteReplay(string interactionKey)
        {
            if (interactionKey == null || interactionKey.Length == 0)
            {
                return false;
            }

            if (IsNeverReplay(interactionKey))
            {
                return false;
            }

            if (interactionKey == "Terrain.GoHere" || interactionKey.Contains("Terrain+GoHere"))
            {
                return true;
            }

            if (MatchesPrefixList(interactionKey, SocialRouteDefinitionPrefixes))
            {
                return true;
            }

            return RoutingInteractionFilter.IsRoutingInteraction(interactionKey);
        }

        private static bool MatchesPrefixList(string interactionKey, string[] prefixes)
        {
            for (int i = 0; i < prefixes.Length; i++)
            {
                string prefix = prefixes[i];
                if (interactionKey == prefix + "Definition")
                {
                    return true;
                }

                if (interactionKey.Length > prefix.Length && interactionKey.StartsWith(prefix))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
