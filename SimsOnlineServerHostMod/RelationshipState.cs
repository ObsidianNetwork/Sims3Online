// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    // a pair's relationship, as the server holds it, for the client's own Relationship object. The HUD's bar and
    // line read liking and the short-term context; the pie menu reads the long-term state (a stranger's menu lacks every
    // acquaintance social). The engine derives that state from liking AND the pair's interaction bits
    // (LongTermRelationship.CalculateLTRState: Acquaintance needs HasMet), so liking alone left every client at Stranger.
    // The menu also reads when the pair last greeted and talked: an acquaintance never greeted gets only the greetings
    // (Sim.NeedsToBeGreeted reads Relationship.WhenLastGreeted, which SocialComponent.StartSocializingWith sets), and one
    // that has not talked for 24 h only the new-conversation socials (SocialComponent.GetAllInteractionsForPieMenu reads
    // WhenLastTalked). The server sends, from its prop poll, on change and every 10 s:
    //   Rl|simA|simB|liking|stc|commodity|commodityValue|perceivedSim|ltr|bits|stateStartedTicks|highestLiking|lastGreetedTicks|lastTalkedTicks[|traitsA|traitsB]
    // The first nine fields are the 2026-10-14 line; a line with only those still reads (HasLtr false). The client sets
    // the long-term fields the engine's own CopyLtr copies, without UpdateLTR (no re-derivation, no state-change effects),
    // and the two times. It also appends each Sim's known trait GUIDs (what A knows about B, and B about A),
    // so trait-based active topics (Talk About Great Outdoors, etc.) appear in the client pie menu.
    public sealed class RelationshipState
    {
        public const string Kind = "Rl";

        public ulong SimA;
        public ulong SimB;
        public float Liking;
        public int Stc;
        public int Commodity;
        public float CommodityValue;
        public ulong PerceivedSim;
        public bool HasLtr;
        public int Ltr;
        public uint Bits;
        public long StateStartedTicks;
        public float HighestLiking;
        public long LastGreetedTicks;
        public long LastTalkedTicks;
        public bool HasTraits;
        public List<ulong> TraitsA;
        public List<ulong> TraitsB;

        public RelationshipState()
        {
            TraitsA = new List<ulong>();
            TraitsB = new List<ulong>();
        }

        public string Line()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(Kind);
            sb.Append('|').Append(SimA.ToString(c)).Append('|').Append(SimB.ToString(c))
                .Append('|').Append(Liking.ToString("F1", c)).Append('|').Append(Stc.ToString(c))
                .Append('|').Append(Commodity.ToString(c)).Append('|').Append(CommodityValue.ToString("F1", c))
                .Append('|').Append(PerceivedSim.ToString(c));
            if (HasLtr)
            {
                sb.Append('|').Append(Ltr.ToString(c)).Append('|').Append(Bits.ToString(c))
                    .Append('|').Append(StateStartedTicks.ToString(c)).Append('|').Append(HighestLiking.ToString("F1", c))
                    .Append('|').Append(LastGreetedTicks.ToString(c)).Append('|').Append(LastTalkedTicks.ToString(c));
                if (HasTraits)
                {
                    sb.Append('|').Append(FormatTraits(TraitsA)).Append('|').Append(FormatTraits(TraitsB));
                }
            }

            return sb.ToString();
        }

        private static string FormatTraits(List<ulong> traits)
        {
            if (traits == null || traits.Count == 0)
            {
                return "-";
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < traits.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(traits[i].ToString(c));
            }

            return sb.ToString();
        }

        // A trait list as FormatTraits writes it: "-" or comma-separated GUIDs. Any other item refuses the whole line, as
        // ConversationState.Parse refuses a bad member or topic.
        private static bool ParseTraits(string s, out List<ulong> list)
        {
            list = new List<ulong>();
            if (s == "-")
            {
                return true;
            }

            if (s == null || s.Length == 0)
            {
                return false;
            }

            string[] items = s.Split(',');
            CultureInfo c = CultureInfo.InvariantCulture;
            for (int i = 0; i < items.Length; i++)
            {
                ulong val;
                if (!ulong.TryParse(items[i], NumberStyles.None, c, out val))
                {
                    return false;
                }

                list.Add(val);
            }

            return true;
        }

        // A ring line as the replay splits it: f[0] the ring sequence, f[1] "Rl", then the fields above. A malformed line
        // reads as nothing; a nine-field line reads with HasLtr false; a 15-field line reads with HasLtr true, HasTraits false;
        // a 17-field line reads with HasLtr true, HasTraits true.
        public static bool Parse(string[] f, out RelationshipState state)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            state = null;
            if (f == null || f.Length < 9 || f[1] != Kind)
            {
                return false;
            }

            RelationshipState s = new RelationshipState();
            if (!ulong.TryParse(f[2], NumberStyles.None, c, out s.SimA) || s.SimA == 0uL
                || !ulong.TryParse(f[3], NumberStyles.None, c, out s.SimB) || s.SimB == 0uL
                || !float.TryParse(f[4], NumberStyles.Float, c, out s.Liking)
                || !int.TryParse(f[5], NumberStyles.Integer, c, out s.Stc)
                || !int.TryParse(f[6], NumberStyles.Integer, c, out s.Commodity)
                || !float.TryParse(f[7], NumberStyles.Float, c, out s.CommodityValue)
                || !ulong.TryParse(f[8], NumberStyles.None, c, out s.PerceivedSim))
            {
                return false;
            }

            if (f.Length == 15 || f.Length == 17)
            {
                if (!int.TryParse(f[9], NumberStyles.Integer, c, out s.Ltr)
                    || !uint.TryParse(f[10], NumberStyles.None, c, out s.Bits)
                    || !long.TryParse(f[11], NumberStyles.Integer, c, out s.StateStartedTicks)
                    || !float.TryParse(f[12], NumberStyles.Float, c, out s.HighestLiking)
                    || !long.TryParse(f[13], NumberStyles.Integer, c, out s.LastGreetedTicks)
                    || !long.TryParse(f[14], NumberStyles.Integer, c, out s.LastTalkedTicks))
                {
                    return false;
                }

                s.HasLtr = true;
                if (f.Length == 17)
                {
                    if (!ParseTraits(f[15], out s.TraitsA) || !ParseTraits(f[16], out s.TraitsB))
                    {
                        return false;
                    }

                    s.HasTraits = true;
                }
            }
            else if (f.Length != 9)
            {
                return false;
            }

            state = s;
            return true;
        }
    }
}
