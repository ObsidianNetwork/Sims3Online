using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // Live-tunable numbers (scripts/tune.py; SACS_RECORDER tune [name value]): a constant read through Get keeps its
    // default until a tune sets it in the running game, so a threshold or a delay is tried without a build or a boot.
    // Not saved: a restart is back to the defaults (change the default in code once a value is settled). Names are
    // dotted, by area: mirror.settle, walk.overrun.share.
    public static class Tunables
    {
        private static readonly Dictionary<string, double> sValues = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> sDefaults = new Dictionary<string, double>();

        // Bumped by every change Set makes: a value derived from the tunables is cached against it (CachedTunable, the
        // SACS hooks' maps in SacsRecorder), so a hot path compares one int instead of reading the dictionary.
        private static int sVersion;

        public static int Version
        {
            get { return sVersion; }
        }

        public static double Get(string name, double fallback)
        {
            sDefaults[name] = fallback;
            double value;
            return sValues.TryGetValue(name, out value) ? value : fallback;
        }

        public static float Get(string name, float fallback)
        {
            return (float)Get(name, (double)(decimal)fallback); // through decimal: 0.05f lists as 0.05
        }

        public static TimeSpan Seconds(string name, double fallback)
        {
            return TimeSpan.FromSeconds(Get(name, fallback));
        }

        // Whether any tune is set, and a set value without registering a default: for names made at run time
        // (sacs.force.<parameter hash>), which would otherwise fill the report.
        public static bool AnySet
        {
            get { return sValues.Count > 0; }
        }

        public static bool TryGetSet(string name, out double value)
        {
            return sValues.TryGetValue(name, out value);
        }

        // The first set tunable whose name starts with the prefix: for a value too large for a tune (the game parses
        // tunes at float precision, so a 32-bit hash loses its low bits), carried in the name instead
        // (sacs.steer.<hub>.<variant>).
        public static bool TryGetSetPrefix(string prefix, out string name, out double value)
        {
            foreach (KeyValuePair<string, double> entry in sValues)
            {
                if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    name = entry.Key;
                    value = entry.Value;
                    return true;
                }
            }

            name = null;
            value = 0;
            return false;
        }

        // The set tunables in their own order, copied: for a cache rebuilt after a tune changes, not for a hot path.
        public static List<KeyValuePair<string, double>> SetEntries()
        {
            return new List<KeyValuePair<string, double>>(sValues);
        }

        // A name with no value (or "default") goes back to its default.
        public static string Set(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "error=no name";
            }

            double parsed;
            if (string.IsNullOrEmpty(value) || value == "default")
            {
                sValues.Remove(name);
            }
            else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                sValues[name] = parsed;
            }
            else
            {
                return "error=not a number: " + value;
            }

            sVersion++;
            EventQueueRecorder.Sync(); // the SACS event recorder follows sacs.catchup
            SacsPicks.Sync();          // SimLink's switches follow sacs.keyed-picks (tier 3), sacs.node-order (A'),
                                       // sacs.keyed-random and sacs.random-log (item 3), gc.gate and
                                       // gc.gate-interval-ms (the GC rate gate)
            return Report();
        }

        // name=value(default) for every tunable read so far, and any set before its first read.
        public static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            SortedDictionary<string, bool> names = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (string name in sDefaults.Keys)
            {
                names[name] = true;
            }

            foreach (string name in sValues.Keys)
            {
                names[name] = true;
            }

            List<string> rows = new List<string>();
            foreach (string name in names.Keys)
            {
                double value;
                double fallback;
                bool set = sValues.TryGetValue(name, out value);
                bool known = sDefaults.TryGetValue(name, out fallback);
                rows.Add(name + "=" + (set ? value : fallback).ToString("R", c) + (set ? " (default " + (known ? fallback.ToString("R", c) : "?") + ")" : ""));
            }

            return "tunables=" + rows.Count.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }
    }

    // A tunable that a hot path reads: a SACS hook running on a game task's stack, on every yes/no SetParam or SACS event.
    // Get hashes the name, writes the default and, for a float default, converts through decimal on every call, and the
    // hooks made those calls whenever any tune was set (Tunables.AnySet). The deeper stacks put interpreter
    // frames on a stack chunk's end far more often, where an engine overrun zeroes the chunk's GC descriptor.
    // This reads Get again only after a tune changes, so a call costs one int
    // compare. Unset tunables still read the fallback without registering it, as Get behind AnySet did.
    internal sealed class CachedTunable
    {
        private readonly string mName;
        private readonly float mFallback;
        private int mVersion = -1;
        private float mValue;

        internal CachedTunable(string name, float fallback)
        {
            mName = name;
            mFallback = fallback;
        }

        internal float Value
        {
            get
            {
                int version = Tunables.Version;
                if (mVersion != version)
                {
                    mValue = Tunables.AnySet ? Tunables.Get(mName, mFallback) : mFallback;
                    mVersion = version;
                }

                return mValue;
            }
        }
    }
}
