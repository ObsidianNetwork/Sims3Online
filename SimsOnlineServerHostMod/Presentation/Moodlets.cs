using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using SimsOnlineServerHost;

namespace SimsOnline.Presentation
{
    // Moodlets on a client (the server side, ServerHostMain.PublishBuffs, sends Bf|sim|guid:origin:minutesLeft:uiMinutes,...):
    // the copy gets the server's set and times, and the HUD shows the server's time left through the fork's moodlet-time
    // hook (PresenterHook.DisplayTimeout), counted down by the sim minutes since the line arrived.
    internal static class Moodlets
    {
        private static long sAdded;
        private static long sRemoved;
        private static long sForced;
        private static FieldInfo sBuffTemplates;
        private static MethodInfo sAddBuffInstance;

        // Per copy and buff ("localSim:guid"): the server's UI minutes and the sim minute they arrived.
        private static readonly Dictionary<string, float[]> sUiTimes = new Dictionary<string, float[]>();
        private static bool sDisplayHooked;

        internal static void Reset()
        {
            sUiTimes.Clear();
            // The hook points into this assembly: a reloaded module hooks its own on its first Bf line.
            try
            {
                Type hook = PresentationApi.PresenterHook();
                FieldInfo field = hook == null ? null : hook.GetField("DisplayTimeout");
                if (field != null)
                {
                    field.SetValue(null, null);
                }
            }
            catch (Exception)
            {
                // Best effort: the next Bf line points the hook at this module again.
            }

            sDisplayHooked = false;
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "+" + sAdded.ToString(c) + "/-" + sRemoved.ToString(c) + "/forced:" + sForced.ToString(c) + "/times:" + sUiTimes.Count.ToString(c);
        }

        private static float SimMinutesNow()
        {
            return SimClock.ConvertFromTicks(SimClock.CurrentTicks, TimeUnit.Minutes);
        }

        // A copy's buffs are the server's. When this game's own admission rules refuse one (a buff it already has blocks
        // it, BuffManager.AddBuff's ShouldBlock; or its ShouldAdd test reads this copy's state: Well Rested refused
        // behind Very Hungry, which the server's Sim got in the other order), it goes in the way the game re-adds buffs
        // after travel: BuffManager.AddBuff(BuffInstance), a clone of the buff's template with no admission checks.
        private static bool AddServerBuff(Sim sim, ulong guid, Origin origin)
        {
            try
            {
                if (sBuffTemplates == null)
                {
                    sBuffTemplates = typeof(BuffManager).BaseType.GetField("sDictionary", BindingFlags.NonPublic | BindingFlags.Static);
                    sAddBuffInstance = typeof(BuffManager).GetMethod("AddBuff", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(BuffInstance) }, null);
                }

                Dictionary<ulong, BuffInstance> templates = sBuffTemplates == null ? null : sBuffTemplates.GetValue(null) as Dictionary<ulong, BuffInstance>;
                BuffInstance template;
                if (templates == null || sAddBuffInstance == null || !templates.TryGetValue(guid, out template) || template == null)
                {
                    return false;
                }

                BuffInstance copy = template.Clone();
                copy.SetTargetSim(sim.SimDescription);
                copy.mBuffOrigin = origin;
                bool added = (bool)sAddBuffInstance.Invoke(sim.BuffManager, new object[] { copy });
                if (added)
                {
                    sForced++;
                }

                return added;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // PresenterHook.DisplayTimeout: the server's time for a buff of the selected Sim (the Sim whose moodlets the HUD
        // shows), counted down since it arrived; NaN for anything else (the game computes it).
        private static float DisplayTime(object instance)
        {
            BuffInstance buff = instance as BuffInstance;
            Sim sim = PlumbBob.SelectedActor;
            if (buff == null || sim == null || sim.BuffManager == null || !ReferenceEquals(sim.BuffManager.GetElement(buff.Guid), buff))
            {
                return float.NaN;
            }

            float[] time;
            if (!sUiTimes.TryGetValue(sim.ObjectId.Value.ToString(CultureInfo.InvariantCulture) + ":" + ((ulong)buff.Guid).ToString(CultureInfo.InvariantCulture), out time))
            {
                return float.NaN;
            }

            if (time[0] < 0f)
            {
                return time[0];
            }

            float elapsed = Math.Max(0f, SimMinutesNow() - time[1]);
            return Math.Max(0f, time[0] - elapsed);
        }

        private static void HookDisplay()
        {
            if (sDisplayHooked)
            {
                return;
            }

            Type hook = PresentationApi.PresenterHook();
            if (hook == null)
            {
                return;
            }

            sDisplayHooked = true;
            FieldInfo field = hook.GetField("DisplayTimeout");
            if (field != null)
            {
                field.SetValue(null, new Converter<object, float>(DisplayTime));
            }
        }

        internal static bool Replay(string[] f)
        {
            if (f.Length < 4)
            {
                return false;
            }

            CultureInfo c = CultureInfo.InvariantCulture;
            Sim sim = PresentationApi.ReplayObject(ulong.Parse(f[2], c)).ObjectFromId<Sim>();
            if (sim == null || sim.BuffManager == null)
            {
                return false;
            }

            Dictionary<ulong, string[]> target = new Dictionary<ulong, string[]>();
            if (f[3] != "-")
            {
                foreach (string entry in f[3].Split(','))
                {
                    string[] e = entry.Split(':');
                    if (e.Length >= 3)
                    {
                        target[ulong.Parse(e[0], c)] = e;
                    }
                }
            }

            try
            {
                List<ulong> current = new List<ulong>();
                foreach (BuffInstance buff in sim.BuffManager.List)
                {
                    current.Add((ulong)buff.Guid);
                }

                for (int i = 0; i < current.Count; i++)
                {
                    if (!target.ContainsKey(current[i]))
                    {
                        sim.BuffManager.RemoveElement((BuffNames)current[i]);
                        sRemoved++;
                    }
                }

                foreach (KeyValuePair<ulong, string[]> entry in target)
                {
                    Origin origin = (Origin)ulong.Parse(entry.Value[1], c);
                    if (!sim.BuffManager.HasElement((BuffNames)entry.Key)
                        && (sim.BuffManager.AddElement(entry.Key, origin) || AddServerBuff(sim, entry.Key, origin)))
                    {
                        sAdded++;
                    }

                    BuffInstance added = sim.BuffManager.GetElement((BuffNames)entry.Key);
                    float minutes;
                    if (added != null && float.TryParse(entry.Value[2], NumberStyles.Float, c, out minutes))
                    {
                        added.TimeoutCount = minutes;
                    }

                    float uiMinutes;
                    if (entry.Value.Length > 3 && float.TryParse(entry.Value[3], NumberStyles.Float, c, out uiMinutes))
                    {
                        sUiTimes[sim.ObjectId.Value.ToString(c) + ":" + entry.Key.ToString(c)] = new float[] { uiMinutes, SimMinutesNow() };
                    }
                }

                // Times of buffs the server's Sim no longer has go with them.
                string prefix = sim.ObjectId.Value.ToString(c) + ":";
                List<string> stale = new List<string>();
                foreach (string key in sUiTimes.Keys)
                {
                    if (key.StartsWith(prefix, StringComparison.Ordinal) && !target.ContainsKey(ulong.Parse(key.Substring(prefix.Length), c)))
                    {
                        stale.Add(key);
                    }
                }

                for (int i = 0; i < stale.Count; i++)
                {
                    sUiTimes.Remove(stale[i]);
                }

                HookDisplay();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
