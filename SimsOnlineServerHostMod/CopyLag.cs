using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Actors;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Client side: how far every copy is from the server's Sim (sync architecture review 2026-11-15, stage 0b).
    //
    // A ride was read pass or fail from one watched Sim. The copies that wedged were the ones nobody watched. The server
    // already sends each moving Sim's place a few times a second (Pz); the walks module hands each one here with the
    // copy's place at the moment the line is applied, which is the same point on the client's timeline. So every copy
    // in every ride has a number, split by whether the copy was in a walk of its own.
    //
    // Nothing is corrected here. telemetry.epoch: a new value starts the counts over (set it when a ride starts).
    // Public: the walks module is its own assembly.
    public static class CopyLag
    {
        private sealed class Entry
        {
            public long Walk;
            public double WalkSum;
            public float WalkMax;
            public long WalkOver03;
            public long WalkOver1;
            public long Rest;
            public float RestMax;
            public long RestOver03;
            public long Ride;
            public float RideMax;
        }

        private static readonly Dictionary<ulong, Entry> sEntries = new Dictionary<ulong, Entry>();
        private static double sEpoch;

        public static void Note(ulong sim, float distance, bool walking)
        {
            Note(sim, distance, walking ? 1 : 0);
        }

        // state: 0 neither, 1 in a walk of its own, 2 riding (slotted in something that moves, a car: the distance is
        // then the carrier's playout hold times its speed, not an error of the copy's own).
        public static void Note(ulong sim, float distance, int state)
        {
            bool walking = state == 1;
            try
            {
                double epoch = Tunables.Get("telemetry.epoch", 0.0);
                if (epoch != sEpoch)
                {
                    sEpoch = epoch;
                    sEntries.Clear();
                }

                if (float.IsNaN(distance) || float.IsInfinity(distance))
                {
                    return;
                }

                Entry entry;
                if (!sEntries.TryGetValue(sim, out entry))
                {
                    if (sEntries.Count >= 256)
                    {
                        return;
                    }

                    entry = new Entry();
                    sEntries[sim] = entry;
                }

                if (state == 2)
                {
                    entry.Ride++;
                    if (distance > entry.RideMax) entry.RideMax = distance;
                }
                else if (walking)
                {
                    entry.Walk++;
                    entry.WalkSum += distance;
                    if (distance > entry.WalkMax) entry.WalkMax = distance;
                    if (distance > 0.3f) entry.WalkOver03++;
                    if (distance > 1f) entry.WalkOver1++;
                }
                else
                {
                    entry.Rest++;
                    if (distance > entry.RestMax) entry.RestMax = distance;
                    if (distance > 0.3f) entry.RestOver03++;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }
        }

        private static string NameOf(ulong sim)
        {
            try
            {
                Sim found = new ObjectGuid(sim).ObjectFromId<Sim>();
                if (found != null && found.SimDescription != null)
                {
                    return found.FirstName;
                }
            }
            catch (Exception)
            {
                SwallowedErrors.Note();
            }

            return sim.ToString(CultureInfo.InvariantCulture);
        }

        // One line: the totals over every copy, then the copies furthest off while walking and while not.
        public static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            long walk = 0, over03 = 0, over1 = 0, rest = 0, restOver = 0;
            double sum = 0.0;
            ulong worstWalk = 0, worstRest = 0;
            float worstWalkMax = 0f, worstRestMax = 0f;
            foreach (KeyValuePair<ulong, Entry> pair in sEntries)
            {
                Entry e = pair.Value;
                walk += e.Walk; sum += e.WalkSum; over03 += e.WalkOver03; over1 += e.WalkOver1;
                rest += e.Rest; restOver += e.RestOver03;
                if (e.WalkMax > worstWalkMax) { worstWalkMax = e.WalkMax; worstWalk = pair.Key; }
                if (e.RestMax > worstRestMax) { worstRestMax = e.RestMax; worstRest = pair.Key; }
            }

            return "copies " + sEntries.Count.ToString(c)
                + " / walking samples " + walk.ToString(c) + " mean " + (walk == 0 ? "-" : (sum / walk).ToString("F2", c)) + " m"
                + " over 0.3 m " + (walk == 0 ? "-" : (100.0 * over03 / walk).ToString("F0", c)) + "% over 1 m " + (walk == 0 ? "-" : (100.0 * over1 / walk).ToString("F0", c)) + "%"
                + " worst " + (worstWalk == 0 ? "-" : NameOf(worstWalk) + " " + worstWalkMax.ToString("F2", c) + " m")
                + " / other samples " + rest.ToString(c) + " over 0.3 m " + (rest == 0 ? "-" : (100.0 * restOver / rest).ToString("F0", c)) + "%"
                + " worst " + (worstRest == 0 ? "-" : NameOf(worstRest) + " " + worstRestMax.ToString("F2", c) + " m");
        }

        // Each copy with samples, worst walking error first ("Name walk n mean max over1 | other n max"), up to max.
        public static string[] Rows(int max)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<KeyValuePair<ulong, Entry>> all = new List<KeyValuePair<ulong, Entry>>(sEntries);
            all.Sort(delegate(KeyValuePair<ulong, Entry> a, KeyValuePair<ulong, Entry> b)
            {
                float x = Math.Max(a.Value.WalkMax, a.Value.RestMax), y = Math.Max(b.Value.WalkMax, b.Value.RestMax);
                return y.CompareTo(x);
            });
            List<string> rows = new List<string>();
            for (int i = 0; i < all.Count && i < max; i++)
            {
                Entry e = all[i].Value;
                rows.Add(NameOf(all[i].Key) + ": walking " + e.Walk.ToString(c) + " samples mean " + (e.Walk == 0 ? "-" : (e.WalkSum / e.Walk).ToString("F2", c))
                    + " max " + e.WalkMax.ToString("F2", c) + " m over 1 m " + e.WalkOver1.ToString(c)
                    + "; other " + e.Rest.ToString(c) + " samples max " + e.RestMax.ToString("F2", c) + " m over 0.3 m " + e.RestOver03.ToString(c)
                    + "; riding " + e.Ride.ToString(c) + " samples max " + e.RideMax.ToString("F2", c) + " m");
            }

            return rows.ToArray();
        }
    }
}
