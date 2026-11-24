using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Late join for the engine boundaries (docs/architecture-deep-dive/boundary-seams.md). The stream carries changes; a client that
    // joins later never saw them (a prop's hidden flags or model, a meter's bone position, a looping sound, a walk under
    // way, a balloon on screen). Each recorder also keeps the latest line per state key here, in the order of last
    // change, and the late-join snapshot pages them out (SACS_RECORDER snapshot lists the pseudo driver Handle first;
    // snapshot-driver Handle pages these lines, kept to the snapshot's Sims and the props of interest). Live effects come
    // whole from VfxRecorder. Bounded: when full, the entries of objects that no longer exist go first (the server
    // records the whole town, and its passing props would otherwise push out a Sim's long-lived state), then the oldest.
    // A transform is not the last script placement but where each joining Sim and prop is when the snapshot is taken:
    // walks, animation and slots move objects without a call. The recorders also ask Repeat here before sending, so a call that repeats the object's previous line (a flag
    // set every frame, a run of resets) is not sent at all.
    internal static class BoundaryState
    {
        internal const uint Handle = 0xFFFFFFF0u;
        private const int Capacity = 16384;

        private static readonly LinkedList<KeyValuePair<string, string>> sOrder = new LinkedList<KeyValuePair<string, string>>();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, string>>> sByKey =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, string>>>();
        internal static long Remembered;
        internal static long Repeats;

        // The previous boundary line per object (any boundary kind), for Repeat.
        private static readonly Dictionary<string, string> sLastLine = new Dictionary<string, string>();

        // True when the line repeats the object's previous boundary line with nothing recorded for the object in between:
        // a state setter called again with the same value. It changes nothing, so the recorder does not send it. canSkip
        // false only notes the line: an action (a clip played again, a transition restarted) or a transform (an animation
        // or a walk may have moved the object since).
        internal static bool Repeat(string line, bool canSkip)
        {
            try
            {
                int a = line.IndexOf('|');
                int b = a < 0 ? -1 : line.IndexOf('|', a + 1);
                if (b < 0)
                {
                    return false;
                }

                string obj = line.Substring(a + 1, b - a - 1);
                string last;
                if (canSkip && sLastLine.TryGetValue(obj, out last) && string.Equals(last, line, StringComparison.Ordinal))
                {
                    Repeats++;
                    return true;
                }

                if (sLastLine.Count >= Capacity * 4)
                {
                    sLastLine.Clear();
                }

                sLastLine[obj] = line;
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return false;
        }

        // Key: the line's first keyFields fields (kind|object|...), so a later change of the same state replaces it.
        internal static void Remember(string line, int keyFields)
        {
            try
            {
                string key = KeyOf(line, keyFields);
                LinkedListNode<KeyValuePair<string, string>> node;
                if (sByKey.TryGetValue(key, out node))
                {
                    sOrder.Remove(node);
                }

                if (sOrder.Count >= Capacity && SweepDead() < Capacity / 8)
                {
                    // Mostly live state: make room for a while, oldest first, so the next sweep is not on the next call.
                    while (sOrder.Count >= Capacity - Capacity / 8)
                    {
                        sByKey.Remove(sOrder.First.Value.Key);
                        sOrder.RemoveFirst();
                    }
                }

                sByKey[key] = sOrder.AddLast(new KeyValuePair<string, string>(key, line));
                Remembered++;
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        // Drops the entries of objects that no longer exist; returns how many went.
        private static int SweepDead()
        {
            Dictionary<string, bool> alive = new Dictionary<string, bool>();
            List<LinkedListNode<KeyValuePair<string, string>>> gone = new List<LinkedListNode<KeyValuePair<string, string>>>();
            for (LinkedListNode<KeyValuePair<string, string>> node = sOrder.First; node != null; node = node.Next)
            {
                string key = node.Value.Key;
                if (key.StartsWith("En|0|", StringComparison.Ordinal))
                {
                    continue;
                }
                int a = key.IndexOf('|');
                int b = a < 0 ? -1 : key.IndexOf('|', a + 1);
                string obj = b < 0 ? (a < 0 ? string.Empty : key.Substring(a + 1)) : key.Substring(a + 1, b - a - 1);
                bool valid;
                if (!alive.TryGetValue(obj, out valid))
                {
                    ulong id;
                    valid = ulong.TryParse(obj, NumberStyles.None, CultureInfo.InvariantCulture, out id) && Objects.IsValid(new ObjectGuid(id));
                    alive[obj] = valid;
                }

                if (!valid)
                {
                    gone.Add(node);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                sByKey.Remove(gone[i].Value.Key);
                sOrder.Remove(gone[i]);
                sLastLine.Remove(gone[i].Value.Key.Split('|')[1]);
            }

            Swept += gone.Count;
            return gone.Count;
        }

        internal static long Swept;

        internal static void ForgetKey(string key)
        {
            LinkedListNode<KeyValuePair<string, string>> node;
            if (sByKey.TryGetValue(key, out node))
            {
                sByKey.Remove(key);
                sOrder.Remove(node);
            }
        }

        // Forget every key that starts with the prefix ("Au|obj|", "An|obj|q|").
        internal static void Forget(string prefix)
        {
            if (sOrder.Count == 0)
            {
                return;
            }

            List<LinkedListNode<KeyValuePair<string, string>>> gone = new List<LinkedListNode<KeyValuePair<string, string>>>();
            for (LinkedListNode<KeyValuePair<string, string>> node = sOrder.First; node != null; node = node.Next)
            {
                if (node.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    gone.Add(node);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                sByKey.Remove(gone[i].Value.Key);
                sOrder.Remove(gone[i]);
            }
        }

        // An object that no longer exists: all its state.
        internal static void ForgetObject(ulong id)
        {
            sLastLine.Remove(id.ToString(CultureInfo.InvariantCulture));
            if (sOrder.Count == 0)
            {
                return;
            }

            string mark = "|" + id.ToString(CultureInfo.InvariantCulture) + "|";
            List<LinkedListNode<KeyValuePair<string, string>>> gone = new List<LinkedListNode<KeyValuePair<string, string>>>();
            for (LinkedListNode<KeyValuePair<string, string>> node = sOrder.First; node != null; node = node.Next)
            {
                string key = node.Value.Key + "|";
                int first = key.IndexOf('|');
                if (first >= 0 && key.IndexOf(mark, first, StringComparison.Ordinal) == first)
                {
                    gone.Add(node);
                }
            }

            for (int i = 0; i < gone.Count; i++)
            {
                sByKey.Remove(gone[i].Value.Key);
                sOrder.Remove(gone[i]);
            }
        }

        private static string KeyOf(string line, int keyFields)
        {
            int at = -1;
            for (int i = 0; i < keyFields; i++)
            {
                at = line.IndexOf('|', at + 1);
                if (at < 0)
                {
                    return line;
                }
            }

            return line.Substring(0, at);
        }

        // The snapshot's state lines ("0|" + line): first where each target and prop of interest is now (one transform,
        // not in a slot), then the remembered state of those objects, oldest change first. Live effects (VfxRecorder)
        // follow.
        internal static List<string> Lines(Dictionary<ulong, bool> targets)
        {
            List<string> lines = new List<string>();
            if (targets != null)
            {
                // A joining copy starts from no animation of its own, as the server's Sim did: its save's machines (a Sim
                // reading in the armchair the save left it in) are not the server's. A replayed StopAllAnimation clears
                // them; the client restores only its standing and facial idles, and the history below plays onto that.
                foreach (ulong id in targets.Keys)
                {
                    lines.Add("0|Z|" + id.ToString(CultureInfo.InvariantCulture));
                }

                foreach (ulong id in targets.Keys)
                {
                    AddTransform(id, lines);
                }

                // each target in no conversation now says so (the ConversationState absence line), so a joining
                // copy drops one its save loaded that the server's Sim no longer holds: a conversation that ended while
                // its Sims were out of scope, or before any pass published it, left no end line to send.
                // The publisher's own read and rule mean a Sim it cannot read is never said to be absent here either.
                List<ConversationState> active = new List<ConversationState>();
                Dictionary<ulong, bool> unreadable = new Dictionary<ulong, bool>();
                ServerHostMain.ReadTargetConversations(targets.Keys, active, unreadable);
                List<string> absent = ConversationState.AbsenceLines(targets.Keys,
                    ConversationState.Talking(active, unreadable.Keys), null);
                for (int i = 0; i < absent.Count; i++)
                {
                    lines.Add("0|" + absent[i]);
                }
            }

            foreach (ulong id in SacsRecorder.Props.Keys)
            {
                if (targets == null || !targets.ContainsKey(id))
                {
                    AddTransform(id, lines);
                }
            }

            for (LinkedListNode<KeyValuePair<string, string>> node = sOrder.First; node != null; node = node.Next)
            {
                string line = node.Value.Value;
                string[] f = line.Split('|');
                ulong obj;
                // A lot-keyed world call (Wl: floor features, holiday lights) is every game's: lots are not streamed targets.
                if (f.Length > 1 && f[0] != "Ob" && ulong.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out obj)
                    && (targets == null || f[0] == "Wl" || f[0] == "En" || targets.ContainsKey(obj) || SacsRecorder.Props.ContainsKey(obj)))
                {
                    lines.Add("0|" + line);
                }
            }

            List<string> effects = VfxRecorder.LiveLines(targets);
            for (int i = 0; i < effects.Count; i++)
            {
                lines.Add("0|" + effects[i]);
            }

            return lines;
        }

        private static void AddTransform(ulong id, List<string> lines)
        {
            string now = CurrentTransform(id);
            if (now != null)
            {
                lines.Add("0|" + now);
            }
        }

        // Ob|object|SetTransform:m|... read from the engine now; null when the object is gone or a Sim holds it (its slot
        // lines and the Sim's animation place it). An object in a slot of furniture is included: the clips that use it move
        // it within its slot without a call (a dining chair pulled out by a sit stayed pulled out on the server, while a
        // client that joined later had it pushed in from its save, so the next sit and the props near the Sim differed,
        // 2026-10-15), and a transform set on a slotted object becomes its offset in the parent's frame, still attached
        // (apply-state-mirror.md, native-slot-attach.md).
        private static string CurrentTransform(ulong id)
        {
            try
            {
                ObjectGuid guid = new ObjectGuid(id);
                ObjectGuid parent = Objects.IsValid(guid) ? Slots.GetParentId(guid) : ObjectGuid.InvalidObjectGuid;
                if (!Objects.IsValid(guid) || (parent.IsValid && parent.ObjectFromId<Sims3.Gameplay.Actors.Sim>() != null))
                {
                    return null;
                }

                Matrix44 m = Objects.GetTransform(guid);
                return "Ob|" + id.ToString(CultureInfo.InvariantCulture) + "|SetTransform:m|" + V4(m.right) + "~" + V4(m.up) + "~" + V4(m.at) + "~" + V4(m.pos);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string V4(Vector4 v)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return v.x.ToString("R", c) + "~" + v.y.ToString("R", c) + "~" + v.z.ToString("R", c) + "~" + v.w.ToString("R", c);
        }

        internal static Dictionary<ulong, bool> LastTargets;

        // Paged like a driver's history: from offset, within maxChars; next = -1 at the end.
        internal static List<string> Page(int offset, int maxChars, out int next)
        {
            List<string> all = Lines(LastTargets);
            List<string> page = new List<string>();
            int chars = 0;
            int i = Math.Max(0, offset);
            for (; i < all.Count; i++)
            {
                if (page.Count > 0 && chars + all[i].Length + 1 > maxChars)
                {
                    break;
                }

                page.Add(all[i]);
                chars += all[i].Length + 1;
            }

            next = i < all.Count ? i : -1;
            return page;
        }

        internal static int Count
        {
            get { return sOrder.Count; }
        }
    }
}
