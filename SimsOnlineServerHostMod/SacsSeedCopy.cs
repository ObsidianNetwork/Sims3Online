using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    // tier 1 (docs/engine/sacs-native-picks.md, "Tier 1"): the server's mirrored SACS drivers hand their Random-node
    // seeds to the clients' copies, so a jazz Random node draws the same outcome on both games for as long as both
    // drivers evaluate the same nodes in the same order. Data only: SimLink reads and writes two dwords of the driver
    // (native version 2); no game code changes. On by default at 2 from 2026-10-21 (the user, after Session I's
    // arms 20261021-151932 and 20261021-155514); sacs.seed-copy=0 on both games turns it off, 1 is the copy without
    // the late seeding below.
    //   server: AS|driver|contextSeed|spareSeed right after the driver's A| line, or before its first state call when
    //           the handle did not resolve to a driver at acquire;
    //   client: writes them into its copy (never into a borrowed driver, which has drawn already) and reads them back.
    // A seed of 0 means "reseed from rdtsc on the next draw", so a server driver that has not drawn yet gets a fresh
    // non-zero seed written into it first, and the client gets that one.
    // sacs.seed-copy=2 also seeds a driver acquired while the copy was off, before its next state call (its current
    // LCG state, which the copy then continues from).
    // When the copy turns on, the server gives every driver acquired before then (and not seeded yet) a fresh spare seed
    // and sends it alone (AS|driver|0|spare; the user, after the crash chase). No game code draws from the spare, so the
    // context seeds and the games' own draws stay as they were; it lines up tier 3 and A' keys for drivers that live
    // across the switch (the copy's Idle drivers, for one). With keyed picks on, both games' keyed path picks for those
    // drivers follow the new shared spare from then on, which is the point.
    // A driver restored from the save (SacsRecorder.Adopt, no acquire) gets the same: a fresh spare sent alone, before its
    // next state call while the copy is on.
    // The server keeps the last AS| line per driver (SacsRecorder.Driver.SeedLine). A client that meets the driver later
    // (it becomes a target after its AS| line went by, or a late join's snapshot) gets that line right after the A|
    // line; a client whose driver handle went stale re-applies it with the driver's actors and parameters
    // (ServerHostMain.sReplaySetupLines). Both only while the copy is on; neither reads native memory.
    // Coverage (SACS_RECORDER seed-coverage / seed-sent / seed-reset): the client keeps every driver it mirrors and
    // whether a seed reached it; the server keeps, per handle, whether it sent one, whether the copy was on when the
    // driver was acquired, and whether it was adopted from the save (SacsRecorder.Adopt) rather than acquired.
    // scripts/sacs_picks.py coverage joins the two.
    internal static class SacsSeedCopy
    {
        internal const string Tunable = "sacs.seed-copy";
        private static readonly CachedTunable sOn = new CachedTunable(Tunable, 2f);
        internal static long Sent;
        internal static long Generated;
        internal static long Deferred;
        internal static long Written;
        internal static long Skipped;
        internal static long BorrowedSpares;  // client: spare-only writes into borrowed drivers
        internal static long Reads;           // server: seed reads by sacs.seed-read-loop (no AS line)
        internal static long SpareResent;     // server: fresh spares written and sent (SpareLine), alone or, for Reseeded, with the context
        internal static long Reseeded;        // server: of those, seeded drivers a client met while the copy was off (full AS| line)
        internal static long SpareOnly;       // client: spare-only writes into mirrors the copy acquired itself
        internal static long Failed;
        private static uint sMix = 0x9E3779B9u;
        private static readonly Dictionary<uint, Mirror> sMirrors = new Dictionary<uint, Mirror>();
        private static readonly Dictionary<uint, bool> sEarly = new Dictionary<uint, bool>();
        private static readonly Dictionary<uint, int> sServerSeedState = new Dictionary<uint, int>();
        private static readonly Dictionary<uint, uint> sSentSpare = new Dictionary<uint, uint>();
        private static readonly Dictionary<uint, bool> sAdopted = new Dictionary<uint, bool>(); // server: Adopt, no acquire
        private const int SeedOffAtAcquire = 0;
        private const int SeedSentState = 1;
        private const int SeedPendingState = 2;
        private const int SeedNotRung = 3;
        private const int SeedSpareSent = 4;

        private sealed class Mirror
        {
            public ulong Machine;
            public bool Borrowed;
            public bool Early;
            public int Result = int.MinValue; // the last write's result; MinValue = no AS line applied
            public long Lines;                 // replay lines applied since the last seed-reset
            public int Remaps;                 // earlier mappings of this server handle (a resync, or a reused handle)
            public int PreviousResult = int.MinValue; // the last mapping's write result
            public uint CopyDriver;            // the copy's driver handle
            public uint OwnSpare;              // the copy driver's spare seed when mapped (0: not read yet)
            public uint SeededSpare;           // the server's spare, once written into the copy driver
            public bool SpareOnly;             // the last write was a spare-only line (context seed left the copy's own)
            // option C: the copy driver this mapping replaced and the server spare it carried; the first seeds
            // written on the new driver carry its walker counters over when they bring that spare (SacsWalkerCopy.Carry).
            public uint CarryFrom;
            public uint CarryTag;
        }
        private static readonly string[] sRecent = new string[12];
        private static int sRecentNext;

        // Managed state only: safe whether or not SimLink is registered.
        internal static bool Wanted
        {
            get { return sOn.Value > 0f; }
        }

        internal static bool On
        {
            get { return Wanted && SimLinkShadow.SeedsReady; }
        }

        // Crash chase (after report (c)): sacs.seed-read-loop=N makes the server read both seeds of each driver when it
        // is acquired (where the seed copy reads) and of every live driver N times per bridge tick, ringing nothing.
        // With the copy off, it tells a crash caused by reading seeds from one caused by the AS| traffic.
        internal const string ReadLoopTunable = "sacs.seed-read-loop";
        private static readonly CachedTunable sReadLoop = new CachedTunable(ReadLoopTunable, 0f);

        internal static int ReadLoop
        {
            get
            {
                int loops = (int)sReadLoop.Value;
                return loops != 0 && SimLinkShadow.SeedsReady ? loops : 0;
            }
        }

        // Server: one read of both seeds, as ReadLine does, and nothing else.
        internal static void ReadOnly(uint driverHandle)
        {
            SimLinkShadow.ReadSeed(driverHandle, 0);
            SimLinkShadow.ReadSeed(driverHandle, 1);
            Reads++;
        }

        // (the interpreter crash; the user, after the crash chase): sacs.gc-pressure=S forces a full collection
        // every S seconds on the server, with no seed reads. If more collections raise the crash rate, the freed object
        // is one that only native memory keeps alive, not seed traffic.
        internal const string GcTunable = "sacs.gc-pressure";
        private static readonly CachedTunable sGcPressure = new CachedTunable(GcTunable, 0f);
        internal static long Collections;
        internal static long CollectMs;
        private static long sNextCollect;

        internal static float GcPressure
        {
            get { return sGcPressure.Value; }
        }

        internal static void GcTick()
        {
            float seconds = GcPressure;
            if (seconds <= 0f)
            {
                return;
            }

            long now = DateTime.UtcNow.Ticks;
            if (now < sNextCollect)
            {
                return;
            }

            GC.Collect();
            long after = DateTime.UtcNow.Ticks;
            Collections++;
            CollectMs += (after - now) / TimeSpan.TicksPerMillisecond;
            sNextCollect = after + (long)(seconds * TimeSpan.TicksPerSecond);
        }

        // sacs.seed-copy=2: also seed drivers acquired while the copy was off, before their next state call.
        internal static bool Late
        {
            get { return On && sOn.Value >= 2f; }
        }

        // Server: what happened to a driver's seeds (at acquire, and at every later send).
        internal static void NoteServer(uint driverHandle, bool rung, bool on, bool sent)
        {
            if (sServerSeedState.Count > 16384)
            {
                sServerSeedState.Clear();
                sAdopted.Clear();
            }

            int state = !rung ? SeedNotRung : !on ? SeedOffAtAcquire : sent ? SeedSentState : SeedPendingState;
            int was;
            if (sServerSeedState.TryGetValue(driverHandle, out was) && was == SeedSentState && state != SeedSentState)
            {
                return; // only a new acquire resets a sent driver (see NoteAcquired)
            }

            sServerSeedState[driverHandle] = state;
        }

        // Server: a spare-only line went out for a driver acquired before the copy turned on (a full line stays "sent").
        internal static void NoteSpareSent(uint driverHandle)
        {
            int was;
            if (sServerSeedState.TryGetValue(driverHandle, out was) && was == SeedSentState)
            {
                return;
            }

            if (sServerSeedState.Count > 16384)
            {
                sServerSeedState.Clear();
                sAdopted.Clear();
            }

            sServerSeedState[driverHandle] = SeedSpareSent;
        }

        // Server: a driver handle acquired (a reused handle starts over, adopted or not).
        internal static void NoteAcquired(uint driverHandle, bool rung, bool on, bool sent)
        {
            sServerSeedState.Remove(driverHandle);
            sAdopted.Remove(driverHandle);
            NoteServer(driverHandle, rung, on, sent);
        }

        // Server: the driver came back from the save (SacsRecorder.Adopt, after its NoteAcquired), with no acquire.
        internal static void NoteAdopted(uint driverHandle)
        {
            sAdopted[driverHandle] = true;
        }

        // Server: "handle:state:spare:adopted" for each handle asked about (state sent, spare-sent, pending,
        // off-at-acquire, not-rung or unknown; spare the one sent, 0 when none was; adopted 1 for a driver restored from
        // the save, 0 for one acquired).
        internal static string SentReport(string handles)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> rows = new List<string>();
            foreach (string part in (handles ?? string.Empty).Split(','))
            {
                uint handle;
                if (!uint.TryParse(part, NumberStyles.Integer, c, out handle))
                {
                    continue;
                }

                int state;
                string text = !sServerSeedState.TryGetValue(handle, out state) ? "unknown"
                    : state == SeedSentState ? "sent" : state == SeedPendingState ? "pending" : state == SeedNotRung ? "not-rung"
                    : state == SeedSpareSent ? "spare-sent" : "off-at-acquire";
                uint spare;
                sSentSpare.TryGetValue(handle, out spare);
                rows.Add(U(handle) + ":" + text + ":" + U(spare) + ":" + (sAdopted.ContainsKey(handle) ? "1" : "0"));
            }

            return "seedSent=" + rows.Count.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }

        // Client: a server driver mapped to a copy driver (acquired, or borrowed from the copy's own game).
        internal static void NoteMapped(uint serverDriver, uint copyDriver, ulong machine, bool borrowed)
        {
            if (sMirrors.Count > 4096)
            {
                List<uint> idle = new List<uint>();
                foreach (KeyValuePair<uint, Mirror> entry in sMirrors)
                {
                    if (entry.Value.Lines == 0)
                    {
                        idle.Add(entry.Key);
                    }
                }

                foreach (uint key in idle)
                {
                    sMirrors.Remove(key);
                }
            }

            // A resync maps the same server driver again: the server sent its seeds once, to the earlier copy driver.
            Mirror previous;
            sMirrors.TryGetValue(serverDriver, out previous);
            Mirror mirror = new Mirror();
            mirror.Machine = machine;
            mirror.Borrowed = borrowed;
            mirror.Early = sEarly.ContainsKey(serverDriver);
            mirror.CopyDriver = copyDriver;
            mirror.OwnSpare = ReadSpare(copyDriver);
            if (previous != null)
            {
                mirror.Remaps = previous.Remaps + 1;
                mirror.PreviousResult = previous.Result;
                if (previous.SeededSpare != 0u && previous.CopyDriver != copyDriver)
                {
                    // A re-resolve after kProxyInvalid (or a resync): the replay setup cache's AS| line comes next.
                    mirror.CarryFrom = previous.CopyDriver;
                    mirror.CarryTag = previous.SeededSpare;
                }
            }

            sMirrors[serverDriver] = mirror;
        }

        // The copy driver's spare seed, or 0 (not resolved yet, or SimLink without the seed calls).
        private static uint ReadSpare(uint driverHandle)
        {
            if (!SimLinkShadow.SeedsReady)
            {
                return 0u;
            }

            try
            {
                return (uint)SimLinkShadow.ReadSeed(driverHandle, 1);
            }
            catch (Exception)
            {
                return 0u;
            }
        }

        // Client: every mapped driver, for joining the pick log to the mirrors (scripts/sacs_picks.py). Header
        // "mirrors=<n>", then from the skip-th: serverDriver|copyDriver|machine (hex)|borrowed|lastResult|lines|remaps|
        // ownSpare|seededSpare|spareOnly (ownSpare is read again while 0 and the copy driver still resolves).
        internal static string MirrorsReport(int skip, int maxRows)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> rows = new List<string>();
            int row = 0;
            foreach (KeyValuePair<uint, Mirror> entry in sMirrors)
            {
                if (row++ < skip || rows.Count >= maxRows)
                {
                    continue;
                }

                Mirror m = entry.Value;
                if (m.OwnSpare == 0u)
                {
                    m.OwnSpare = ReadSpare(m.CopyDriver);
                }

                rows.Add(U(entry.Key) + "|" + U(m.CopyDriver) + "|" + m.Machine.ToString("X16", c) + "|" + (m.Borrowed ? "1" : "0") + "|"
                    + (m.Result == int.MinValue ? "-" : m.Result.ToString(c)) + "|" + m.Lines.ToString(c) + "|" + m.Remaps.ToString(c) + "|"
                    + U(m.OwnSpare) + "|" + U(m.SeededSpare) + "|" + (m.SpareOnly ? "1" : "0"));
            }

            return "mirrors=" + sMirrors.Count.ToString(c) + "\n" + string.Join("\n", rows.ToArray());
        }

        // Client: an AS| line for a server driver the copy has not mapped (yet).
        internal static void NoteEarly(uint serverDriver)
        {
            if (sEarly.Count > 4096)
            {
                sEarly.Clear();
            }

            sEarly[serverDriver] = true;
        }

        // Client: a replay line applied to a mapped driver.
        internal static void NoteLine(uint serverDriver)
        {
            Mirror mirror;
            if (sMirrors.TryGetValue(serverDriver, out mirror))
            {
                mirror.Lines++;
            }
        }

        internal static string ResetCoverage()
        {
            foreach (Mirror mirror in sMirrors.Values)
            {
                mirror.Lines = 0;
            }

            sEarly.Clear();
            return "seedCoverage=reset";
        }

        // Client: the mirrored drivers that got replay lines since the last seed-reset. Header counts, then one row per
        // driver without a seed, from the skip-th on (pages of one bridge frame): serverDriver|machine (hex)|borrowed|early|
        // lastResult|lines|remaps|previousResult.
        internal static string CoverageReport(int skip, int maxRows)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            int active = 0, seeded = 0, spareOnly = 0, borrowed = 0, borrowedSpare = 0, unseeded = 0;
            int row = 0;
            List<string> rows = new List<string>();
            foreach (KeyValuePair<uint, Mirror> entry in sMirrors)
            {
                Mirror m = entry.Value;
                if (m.Lines == 0)
                {
                    continue;
                }

                active++;
                if (m.Borrowed)
                {
                    borrowed++;
                    if (m.Result == 1)
                    {
                        borrowedSpare++; // the spare seed only (see Apply)
                        continue;
                    }
                }
                else if (m.Result == 1)
                {
                    if (m.SpareOnly)
                    {
                        spareOnly++; // a mirror the copy acquired before the copy turned on: keys line up, draws are its own
                    }
                    else
                    {
                        seeded++;
                    }

                    continue;
                }
                else
                {
                    unseeded++;
                }

                if (row++ >= skip && rows.Count < maxRows)
                {
                    rows.Add(U(entry.Key) + "|" + m.Machine.ToString("X16", c) + "|" + (m.Borrowed ? "1" : "0") + "|" + (m.Early ? "1" : "0")
                        + "|" + (m.Result == int.MinValue ? "-" : m.Result.ToString(c)) + "|" + m.Lines.ToString(c) + "|" + m.Remaps.ToString(c)
                        + "|" + (m.PreviousResult == int.MinValue ? "-" : m.PreviousResult.ToString(c)));
                }
            }

            return "seedCoverage=active:" + active.ToString(c) + ",seeded:" + seeded.ToString(c) + ",spareOnly:" + spareOnly.ToString(c)
                + ",borrowed:" + borrowed.ToString(c) + ",borrowedSpare:" + borrowedSpare.ToString(c) + ",unseeded:" + unseeded.ToString(c) + "\n"
                + string.Join("\n", rows.ToArray());
        }

        // Server: the AS| line for this driver, or null when its handle does not resolve to a driver yet.
        internal static string ReadLine(uint driverHandle)
        {
            int context = SimLinkShadow.ReadSeed(driverHandle, 0);
            int spare = SimLinkShadow.ReadSeed(driverHandle, 1);
            if (context == 0 || spare == 0)
            {
                // Not drawn yet (or not resolved): give it seeds of our own, so the copy can have the same ones.
                int freshContext = context != 0 ? context : NextSeed(driverHandle);
                int freshSpare = spare != 0 ? spare : NextSeed(driverHandle);
                int result = SimLinkShadow.WriteSeeds(driverHandle, freshContext, freshSpare);
                if (result == 0)
                {
                    Deferred++;
                    return null;
                }

                if (result != 1)
                {
                    Failed++;
                    Note("server " + U(driverHandle) + " write=" + result.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                Generated++;
                context = freshContext;
                spare = freshSpare;
            }

            Sent++;
            if (sSentSpare.Count > 16384)
            {
                sSentSpare.Clear();
            }

            sSentSpare[driverHandle] = (uint)spare;
            Note("server " + U(driverHandle) + " ctx=" + U((uint)context) + " spare=" + U((uint)spare));
            return "AS|" + U(driverHandle) + "|" + U((uint)context) + "|" + U((uint)spare);
        }

        // Server, when the copy turns on: AS|driver|0|spare for a driver acquired before then, or null when its handle no
        // longer resolves. The spare is a fresh one, written into the server's driver first: tier 3's key mixes the spare
        // with per-walker request and draw counters that reset when the spare changes (SimLink FindWalker), so both
        // games restart them at the same place in the ring (the copy applies this line in order with the server's
        // requests). Sending the server's old spare would leave the server's counters running from acquire and the
        // copy's restarting at 0. The copy writes the spare alone (a 0 seed is never written).
        internal static string SpareLine(uint driverHandle)
        {
            int spare = NextSeed(driverHandle);
            if (SimLinkShadow.WriteSeeds(driverHandle, 0, spare) != 1)
            {
                return null;
            }

            SpareResent++;
            if (sSentSpare.Count > 16384)
            {
                sSentSpare.Clear();
            }

            sSentSpare[driverHandle] = (uint)spare;
            NoteSpareSent(driverHandle);
            Note("server " + U(driverHandle) + " spare=" + U((uint)spare) + " (copy on)");
            return "AS|" + U(driverHandle) + "|0|" + U((uint)spare);
        }

        // Client: f = seq|AS|serverDriver|contextSeed|spareSeed, applied to the copy's driver. A context seed of 0 is a
        // spare-only line (SpareLine): it never replaces the record of a full seeding.
        internal static void Apply(uint serverDriver, uint clientDriver, bool borrowed, string[] f)
        {
            Mirror mirror;
            sMirrors.TryGetValue(serverDriver, out mirror);
            if (!On || f.Length < 5)
            {
                Skipped++;
                return;
            }

            // A borrowed driver is the copy's own: it takes only the spare seed (+0x34, which the game never uses), so
            // its tier 3 keys line up with the server's; its context seed (+0xa8) stays its own (the user, after
            // report (c)). WriteSeeds leaves a 0 seed unwritten.
            CultureInfo c = CultureInfo.InvariantCulture;
            int context = borrowed ? 0 : unchecked((int)uint.Parse(f[3], c));
            int spare = unchecked((int)uint.Parse(f[4], c));
            bool spareOnly = !borrowed && context == 0;
            if (borrowed)
            {
                BorrowedSpares++;
            }
            else if (spareOnly)
            {
                SpareOnly++;
            }
            int before = SimLinkShadow.ReadSeed(clientDriver, 0);
            int result = SimLinkShadow.WriteSeeds(clientDriver, context, spare);
            if (result == 1)
            {
                Written++;
            }
            else
            {
                Failed++;
            }

            if (mirror != null && !(spareOnly && mirror.Result == 1 && !mirror.SpareOnly))
            {
                mirror.Result = result;
                mirror.SpareOnly = spareOnly;
                mirror.SeededSpare = result == 1 ? (uint)spare : 0u;
            }

            if (mirror != null && result == 1 && mirror.CarryFrom != 0u)
            {
                // option C: the old copy's spare on its replacement, so the old driver's walker counters (aligned
                // with the server's, if they were) carry over. One chance: other seeds are another driver's lineage.
                if ((uint)spare == mirror.CarryTag)
                {
                    SacsWalkerCopy.Carry(serverDriver, mirror.CarryFrom, clientDriver, spare);
                }

                mirror.CarryFrom = 0u;
            }

            Note("client " + U(serverDriver) + "->" + U(clientDriver) + " ctx=" + U((uint)context) + " before=" + U((uint)before)
                + " result=" + result.ToString(c));
        }

        internal static string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string recent = string.Empty;
            for (int i = 0; i < sRecent.Length; i++)
            {
                string entry = sRecent[(sRecentNext + i) % sRecent.Length];
                if (entry != null)
                {
                    recent += (recent.Length == 0 ? string.Empty : ",") + entry;
                }
            }

            return "seedCopy=" + (Wanted ? "on" : "off") + ",ready=" + (SimLinkShadow.SeedsReady ? "1" : "0") + ",sent=" + Sent.ToString(c)
                + ",generated=" + Generated.ToString(c) + ",deferred=" + Deferred.ToString(c) + ",written=" + Written.ToString(c)
                + ",skipped=" + Skipped.ToString(c) + ",failed=" + Failed.ToString(c) + ",borrowedSpares=" + BorrowedSpares.ToString(c)
                + ",readLoop=" + ReadLoop.ToString(c) + ",reads=" + Reads.ToString(c) + ",spareResent=" + SpareResent.ToString(c)
                + ",reseeded=" + Reseeded.ToString(c) + ",spareOnly=" + SpareOnly.ToString(c) + ",gcPressure=" + GcPressure.ToString(c) + ",collections=" + Collections.ToString(c)
                + ",collectMs=" + CollectMs.ToString(c) + ",gcCount=" + GC.CollectionCount(0).ToString(c) + ",recent=[" + recent + "]";
        }

        private static int NextSeed(uint driverHandle)
        {
            // xorshift over the clock, the handle and the previous seed; never 0.
            uint x = sMix ^ (uint)DateTime.UtcNow.Ticks ^ (driverHandle * 0x85EBCA6Bu);
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            sMix = x == 0u ? 0x9E3779B9u : x;
            return unchecked((int)sMix);
        }

        private static void Note(string entry)
        {
            sRecent[sRecentNext] = entry;
            sRecentNext = (sRecentNext + 1) % sRecent.Length;
        }

        private static string U(uint value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
