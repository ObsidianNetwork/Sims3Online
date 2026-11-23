// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    // a pair (or group) in conversation, as the server holds it, for the client's own SocialComponent.
    // The engine's pie menu checks SocialComponent.Conversation:
    //   - Goodbye appears when Conversation != null and households differ (SocialComponent.cs:2096).
    //   - STCData.GetNumSocials returns during-conversation count when Conversation != null.
    //   - SimActiveTopics.BuildUpSortedTopics includes ActiveTopics from the conversation.
    // Wire line:
    //   Cv|simA|simB|inConversation|whoseTurn|whoTalkedLast|extraMembers|topics
    // where:
    //   inConversation: "1" or "0"
    //   extraMembers: comma-separated ulongs or "-"
    //   topics: comma-separated topic strings (name or name:paramSimId:activeSimId) or "-"
    // A topic name carries no '|', ',' or ':' (AddTopic leaves such a topic out); Parse refuses a line whose topics
    // break that shape, the way it refuses any other malformed field.
    // Absence: Cv|X|X|0|0|0|-|- (a self pair, not in conversation) says the server's Sim X holds no
    // conversation; the client detaches whatever X holds. A client that loaded a conversation from its save which the
    // server's Sims ended before any pass published it (or while they were out of scope) has no end line to wait for.
    // An older client reads it as the end of pair X-X, which no Sim holds the other half of, and ignores it.
    public sealed class ConversationState
    {
        public const string Kind = "Cv";

        public ulong SimA;
        public ulong SimB;
        public bool InConversation;
        public ulong WhoseTurn;
        public ulong WhoTalkedLast;
        public List<ulong> ExtraMembers;
        public List<string> Topics;

        public ConversationState()
        {
            ExtraMembers = new List<ulong>();
            Topics = new List<string>();
        }

        // Adds one active topic; a name the line cannot carry is left out (false), never sent half-split.
        public bool AddTopic(string name, ulong paramSim, ulong activeSim)
        {
            if (!IsTopicName(name))
            {
                return false;
            }

            if (paramSim == 0uL && activeSim == 0uL)
            {
                Topics.Add(name);
            }
            else
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                Topics.Add(name + ":" + paramSim.ToString(c) + ":" + activeSim.ToString(c));
            }

            return true;
        }

        public static bool IsTopicName(string name)
        {
            return name != null && name.Trim().Length > 0 && name.IndexOf('|') < 0 && name.IndexOf(',') < 0
                && name.IndexOf(':') < 0;
        }

        // One topic entry as Line writes it: a name alone, or name:paramSimId:activeSimId.
        public static bool IsTopicEntry(string entry)
        {
            if (entry == null)
            {
                return false;
            }

            string[] parts = entry.Split(':');
            if (parts.Length == 1)
            {
                return IsTopicName(parts[0]);
            }

            ulong id;
            CultureInfo c = CultureInfo.InvariantCulture;
            return parts.Length == 3 && IsTopicName(parts[0])
                && ulong.TryParse(parts[1], NumberStyles.None, c, out id) && ulong.TryParse(parts[2], NumberStyles.None, c, out id);
        }

        // The absence line's state for one Sim: see the wire comment above.
        public static ConversationState Absent(ulong sim)
        {
            ConversationState s = new ConversationState();
            s.SimA = sim;
            s.SimB = sim;
            s.InConversation = false;
            return s;
        }

        public bool IsAbsence
        {
            get { return !InConversation && SimA == SimB; }
        }

        // The absence lines for the targets in no conversation (talking: every member of a conversation now), lowest
        // id first; with only, just the targets it holds (the publisher's new targets). Used by the publisher and the
        // late-join snapshot (BoundaryState.Lines).
        public static List<string> AbsenceLines(ICollection<ulong> targets, Dictionary<ulong, bool> talking,
            Dictionary<ulong, bool> only)
        {
            List<ulong> ids = new List<ulong>();
            if (targets != null)
            {
                foreach (ulong id in targets)
                {
                    if (id != 0uL && (talking == null || !talking.ContainsKey(id)) && (only == null || only.ContainsKey(id))
                        && !ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            ids.Sort();
            List<string> lines = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                lines.Add(Absent(ids[i]).Line());
            }

            return lines;
        }

        // The Sims never said to be absent (AbsenceLines' talking), the one rule of the publisher and the late-join
        // snapshot ( review n1: they disagreed on a Sim the server cannot read): every member of a conversation read
        // now, and every unreadable target (ServerHostMain.ReadTargetConversations: no Sim, or its conversation read
        // failed). An absence detaches the client's copy, so the server states one only for a Sim it read holding none.
        public static Dictionary<ulong, bool> Talking(ICollection<ConversationState> active, ICollection<ulong> unreadable)
        {
            Dictionary<ulong, bool> talking = new Dictionary<ulong, bool>();
            if (active != null)
            {
                foreach (ConversationState cs in active)
                {
                    if (cs == null)
                    {
                        continue;
                    }

                    List<ulong> members = cs.Members();
                    for (int i = 0; i < members.Count; i++)
                    {
                        talking[members[i]] = true;
                    }
                }
            }

            if (unreadable != null)
            {
                foreach (ulong id in unreadable)
                {
                    talking[id] = true;
                }
            }

            return talking;
        }

        public string Line()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder(Kind);
            sb.Append('|').Append(SimA.ToString(c))
              .Append('|').Append(SimB.ToString(c))
              .Append('|').Append(InConversation ? "1" : "0")
              .Append('|').Append(WhoseTurn.ToString(c))
              .Append('|').Append(WhoTalkedLast.ToString(c))
              .Append('|');

            if (ExtraMembers != null && ExtraMembers.Count > 0)
            {
                for (int i = 0; i < ExtraMembers.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append(ExtraMembers[i].ToString(c));
                }
            }
            else
            {
                sb.Append('-');
            }

            sb.Append('|');

            if (Topics != null && Topics.Count > 0)
            {
                for (int i = 0; i < Topics.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append(Topics[i]);
                }
            }
            else
            {
                sb.Append('-');
            }

            return sb.ToString();
        }

        // A ring line as the replay splits it: f[0] the ring sequence, f[1] "Cv", then the fields above.
        public static bool Parse(string[] f, out ConversationState state)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            state = null;
            if (f == null || f.Length != 9 || f[1] != Kind)
            {
                return false;
            }

            ConversationState s = new ConversationState();
            if (!ulong.TryParse(f[2], NumberStyles.None, c, out s.SimA) || s.SimA == 0uL
                || !ulong.TryParse(f[3], NumberStyles.None, c, out s.SimB) || s.SimB == 0uL)
            {
                return false;
            }

            if (f[4] == "1")
            {
                s.InConversation = true;
            }
            else if (f[4] == "0")
            {
                s.InConversation = false;
            }
            else
            {
                return false;
            }

            if (!ulong.TryParse(f[5], NumberStyles.None, c, out s.WhoseTurn)
                || !ulong.TryParse(f[6], NumberStyles.None, c, out s.WhoTalkedLast))
            {
                return false;
            }

            if (f[7] != "-")
            {
                string[] extra = f[7].Split(',');
                for (int i = 0; i < extra.Length; i++)
                {
                    ulong memberId;
                    if (ulong.TryParse(extra[i], NumberStyles.None, c, out memberId) && memberId != 0uL)
                    {
                        s.ExtraMembers.Add(memberId);
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if (f[8] != "-")
            {
                string[] topicArr = f[8].Split(',');
                for (int i = 0; i < topicArr.Length; i++)
                {
                    if (!IsTopicEntry(topicArr[i]))
                    {
                        return false;
                    }

                    s.Topics.Add(topicArr[i]);
                }
            }

            state = s;
            return true;
        }

        // Every member: the pair, then the extra members, each once.
        public List<ulong> Members()
        {
            List<ulong> all = new List<ulong>();
            all.Add(SimA);
            if (SimB != SimA)
            {
                all.Add(SimB);
            }

            if (ExtraMembers != null)
            {
                for (int i = 0; i < ExtraMembers.Count; i++)
                {
                    if (!all.Contains(ExtraMembers[i]))
                    {
                        all.Add(ExtraMembers[i]);
                    }
                }
            }

            return all;
        }

        public bool ShouldForwardTo(IDictionary<ulong, bool> ids)
        {
            // Ends retire state already mirrored on a client, even after every member has
            // left that client's current scope. Starts and absence snapshots remain scoped.
            return (!InConversation && !IsAbsence) || NamesAny(ids);
        }

        // Whether any member is one of ids.
        public bool NamesAny(IDictionary<ulong, bool> ids)
        {
            if (ids == null)
            {
                return false;
            }

            List<ulong> all = Members();
            for (int i = 0; i < all.Count; i++)
            {
                if (ids.ContainsKey(all[i]))
                {
                    return true;
                }
            }

            return false;
        }

        // The publisher's key: the whole membership, sorted. A member who leaves or joins makes a new key, so the old
        // conversation's end goes out before the new one starts (review wf_842dd179-926 finding 1: a key of the first two
        // members alone sent no end when a third left).
        public string MemberKey()
        {
            List<ulong> all = Members();
            all.Sort();
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(all[i].ToString(c));
            }

            return sb.ToString();
        }

        // This conversation's end line: the same members, not in conversation, no turn and no topics.
        public ConversationState Ended()
        {
            ConversationState end = new ConversationState();
            end.SimA = SimA;
            end.SimB = SimB;
            end.InConversation = false;
            if (ExtraMembers != null)
            {
                end.ExtraMembers.AddRange(ExtraMembers);
            }

            return end;
        }

        // The client compares in its own ids (the replay maps each server id to its copy), so these take both lists.

        // Whether held (the member ids of the conversation one Sim holds) is exactly the membership all.
        public static bool SameMembers(ICollection<ulong> all, ICollection<ulong> held)
        {
            if (all == null || held == null || held.Count != all.Count)
            {
                return false;
            }

            foreach (ulong id in held)
            {
                if (!all.Contains(id))
                {
                    return false;
                }
            }

            return true;
        }

        // Client, on a start line of membership all: the members of a conversation some member of the line still holds
        // who are not in all; each still pointing at that conversation is detached, so no Sim keeps a conversation that
        // dropped it (review wf_842dd179-926 finding 1).
        public static List<ulong> Leavers(ICollection<ulong> all, ICollection<ulong> held)
        {
            List<ulong> gone = new List<ulong>();
            if (held != null)
            {
                foreach (ulong id in held)
                {
                    if ((all == null || !all.Contains(id)) && !gone.Contains(id))
                    {
                        gone.Add(id);
                    }
                }
            }

            return gone;
        }

        // Client, on an end line of pair a-b: sim's conversation (member ids held) ends only while it still holds the other
        // half of that pair; a Sim that already moved on (A-B ended, A-C started) keeps its new one.
        public static bool HoldsOtherHalf(ulong sim, ICollection<ulong> held, ulong a, ulong b)
        {
            return held != null && ((sim != a && held.Contains(a)) || (sim != b && held.Contains(b)));
        }

        // Client, on X's absence: the other members whose copy still holds X's conversation (holding: every
        // member id holding it now, X included) when fewer than two are left: no conversation (the server publishes none
        // smaller), so they are detached with X. A member that is no target of this client has no line of its own to end
        // it (the save's Iliana+Jennifer, forced join race 2026-11-01 01:06Z: Jennifer detached, Iliana kept it). Two or
        // more left keep theirs.
        public static List<ulong> LeftAlone(ulong absent, ICollection<ulong> holding)
        {
            List<ulong> left = new List<ulong>();
            if (holding != null)
            {
                foreach (ulong id in holding)
                {
                    if (id != absent && !left.Contains(id))
                    {
                        left.Add(id);
                    }
                }
            }

            if (left.Count >= 2)
            {
                left.Clear();
            }

            left.Sort();
            return left;
        }
    }

    // Server: the Cv lines of one publishing pass, from every conversation its targets are in now. Each conversation is
    // keyed by its whole membership (ConversationState.MemberKey); the end of every conversation no longer there goes
    // out before any start or change, so a Sim that moved from one conversation to another in one pass is never ended
    // after its new start, and a member who left ends the old membership before the new one starts.
    // With the targets, a target in no conversation gets an absence line in its first pass as a target and on
    // every resend, with the ends: a client copy that kept a conversation the server never published ends it. A target
    // it could not read (unreadable) gets none (ConversationState.Talking).
    public sealed class ConversationPublisher
    {
        private readonly Dictionary<string, ConversationState> mPublished = new Dictionary<string, ConversationState>();
        private readonly Dictionary<string, string> mLines = new Dictionary<string, string>();
        private Dictionary<ulong, bool> mTargets = new Dictionary<ulong, bool>();

        public int Count
        {
            get { return mPublished.Count; }
        }

        public List<string> Pass(List<ConversationState> active, bool resend)
        {
            return Pass(active, null, resend);
        }

        public List<string> Pass(List<ConversationState> active, ICollection<ulong> targets, bool resend)
        {
            return Pass(active, targets, null, resend);
        }

        public List<string> Pass(List<ConversationState> active, ICollection<ulong> targets, ICollection<ulong> unreadable,
            bool resend)
        {
            List<string> lines = new List<string>();
            Dictionary<string, ConversationState> now = new Dictionary<string, ConversationState>();
            List<string> order = new List<string>();
            if (active != null)
            {
                for (int i = 0; i < active.Count; i++)
                {
                    if (active[i] == null)
                    {
                        continue;
                    }

                    string key = active[i].MemberKey();
                    if (!now.ContainsKey(key))
                    {
                        now[key] = active[i];
                        order.Add(key);
                    }
                }
            }

            List<string> ended = new List<string>();
            foreach (string key in mPublished.Keys)
            {
                if (!now.ContainsKey(key))
                {
                    ended.Add(key);
                }
            }

            ended.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ended.Count; i++)
            {
                lines.Add(mPublished[ended[i]].Ended().Line());
                mPublished.Remove(ended[i]);
                mLines.Remove(ended[i]);
            }

            if (targets != null)
            {
                Dictionary<ulong, bool> talking = ConversationState.Talking(now.Values, unreadable);
                Dictionary<ulong, bool> fresh = null;
                if (!resend)
                {
                    fresh = new Dictionary<ulong, bool>();
                    foreach (ulong id in targets)
                    {
                        if (!mTargets.ContainsKey(id))
                        {
                            fresh[id] = true;
                        }
                    }
                }

                lines.AddRange(ConversationState.AbsenceLines(targets, talking, fresh));
                // An unreadable target stays fresh: its absence goes out in the first pass that reads it idle.
                Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
                foreach (ulong id in targets)
                {
                    if (unreadable == null || !unreadable.Contains(id))
                    {
                        seen[id] = true;
                    }
                }

                mTargets = seen;
            }

            for (int i = 0; i < order.Count; i++)
            {
                string key = order[i];
                string line = now[key].Line();
                string last;
                if (!resend && mLines.TryGetValue(key, out last) && last == line)
                {
                    continue;
                }

                mPublished[key] = now[key];
                mLines[key] = line;
                lines.Add(line);
            }

            return lines;
        }
    }
}
