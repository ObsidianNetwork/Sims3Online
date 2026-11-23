using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // Menu refusals on a client (the server side, ServerHostMain.NoteRefusal, sends
    // Rf|actor|target|hexType|hexName|1 when it refuses a player's pick on the definition's own test, and the same line
    // ending in 0 once that test passes again; MenuRefusal). The table holds the refused entries in this game's ids, and
    // the fork's menu loop asks it through PresenterHook.RefusalFor: an entry the client's own
    // test passes but the server refused shows greyed until the clear. The hook asks twice: with
    // "actor|target|type" (any refused entry of that definition here?) and, only then, with "\n" and the entry's menu text
    // appended (that entry?), so a menu builds no names for definitions the server never refused. An entry older than four
    // sim hours (the server's own limit) is dropped here too, in case its clear was missed.
    internal static class Refusals
    {
        private const float MaxMinutes = 240f;

        // "localActor|localTarget|type\nname" -> the sim minute the refusal arrived.
        private static readonly Dictionary<string, float> sEntries = new Dictionary<string, float>();
        // "localActor|localTarget|type" -> how many entries of that definition are held.
        private static readonly Dictionary<string, int> sByType = new Dictionary<string, int>();
        private static long sRefused;
        private static long sCleared;
        private static long sExpired;
        private static long sMissing;
        private static bool sHooked;

        internal static void Reset()
        {
            sEntries.Clear();
            sByType.Clear();
            // The hook points into this assembly: a reloaded module hooks its own on its first Rf line.
            sHooked = false;
            SetHook(null, 0);
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return sEntries.Count.ToString(c) + "/+" + sRefused.ToString(c) + "/-" + sCleared.ToString(c) + "/expired:" + sExpired.ToString(c)
                + "/missing:" + sMissing.ToString(c) + "/greyed:" + HookCounter("RefusalsGreyed");
        }

        internal static bool Replay(string[] f)
        {
            ulong actor;
            ulong target;
            string type;
            string name;
            bool refused;
            if (!MenuRefusal.Parse(f, out actor, out target, out type, out name, out refused))
            {
                return false;
            }

            ObjectGuid localActor = PresentationApi.ReplayObject(actor);
            ObjectGuid localTarget = PresentationApi.ReplayObject(target);
            if (localActor.ObjectFromId<GameObject>() == null || localTarget.ObjectFromId<GameObject>() == null)
            {
                sMissing++;
                return true;
            }

            string byType = MenuRefusal.Key(localActor.Value, localTarget.Value, type);
            string entry = byType + "\n" + name;
            if (refused)
            {
                if (!sEntries.ContainsKey(entry))
                {
                    int count;
                    sByType.TryGetValue(byType, out count);
                    sByType[byType] = count + 1;
                }

                sEntries[entry] = SimMinutesNow();
                sRefused++;
            }
            else if (Remove(entry))
            {
                sCleared++;
            }

            if (!sHooked)
            {
                sHooked = true;
                SetHook(new Converter<string, string>(Lookup), sEntries.Count);
            }
            else
            {
                SetHook(null, sEntries.Count);
            }

            return true;
        }

        // PresenterHook.RefusalFor: non-null when the server refused that entry (or, without a name, any entry of that
        // definition on that target for that actor). Runs inside the menu loop: never throws.
        private static string Lookup(string query)
        {
            try
            {
                if (query == null)
                {
                    return null;
                }

                if (query.IndexOf('\n') < 0)
                {
                    return sByType.ContainsKey(query) ? "1" : null;
                }

                float since;
                if (!sEntries.TryGetValue(query, out since))
                {
                    return null;
                }

                if (SimMinutesNow() - since > MaxMinutes)
                {
                    Remove(query);
                    sExpired++;
                    SetHook(null, sEntries.Count);
                    return null;
                }

                return "1";
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Remove(string entry)
        {
            if (!sEntries.Remove(entry))
            {
                return false;
            }

            string byType = entry.Substring(0, entry.IndexOf('\n'));
            int count;
            if (sByType.TryGetValue(byType, out count) && count > 1)
            {
                sByType[byType] = count - 1;
            }
            else
            {
                sByType.Remove(byType);
            }

            return true;
        }

        private static float SimMinutesNow()
        {
            return SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Minutes);
        }

        // lookup null leaves the hook's delegate as it is, unless held is 0 on a reset (then it is cleared too).
        private static void SetHook(Converter<string, string> lookup, int held)
        {
            try
            {
                Type hook = PresentationApi.PresenterHook();
                if (hook == null)
                {
                    return;
                }

                FieldInfo forField = hook.GetField("RefusalFor");
                FieldInfo heldField = hook.GetField("RefusalsHeld");
                if (forField != null && (lookup != null || (held == 0 && !sHooked)))
                {
                    forField.SetValue(null, lookup);
                }

                if (heldField != null)
                {
                    heldField.SetValue(null, held);
                }
            }
            catch (Exception)
            {
                // Best effort: without the fork's hook the client's menus keep the game's own answer.
            }
        }

        private static string HookCounter(string name)
        {
            try
            {
                Type hook = PresentationApi.PresenterHook();
                FieldInfo field = hook == null ? null : hook.GetField(name);
                return field == null ? "-" : Convert.ToString(field.GetValue(null), CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "-";
            }
        }
    }
}
