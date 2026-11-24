using System;
using System.Globalization;
using Sims3.Gameplay.Utilities;

namespace SimsOnlineServerHost
{
    // The stream's timeline (sync architecture review 2026-11-15, stage 0a).
    //
    // Until now no line carried the server's time. A client applied a line a fixed hold after it ARRIVED
    // (LineDelay.cs), and the playout buffer guessed the spacing of an object's poses from when they arrived
    // (TransformPlayout.Interval). A batch that arrived late or clumped was therefore shown late or clumped: the
    // ride in series m that stalled and then moved 26.6 m in half a second (motion 162506) had no long frame on
    // either side.
    //
    // Server: ahead of the first line it rings after the clock has moved on (stamp.every-ms, 0 = off by default;
    // when > 0, e.g. 5 ms: once a tick that has lines), the recorder rings a clock line, Ck|<utc ms>|<SimClock ticks>.
    // Every line up to the next Ck happened at that time, to within the tick.
    //
    // Client: each Ck gives one sample of (arrival time - stamp), which is the clocks' offset plus that line's
    // transit. The smallest sample over the last 20 to 30 s is taken as the offset (the fastest transit seen), so a
    // line's effective arrival is stamp + offset: when it would have arrived had nothing delayed it. With
    // playout.stamped 1 the line delay and the playout buffer use that time instead of the real arrival, so what a
    // client shows is the server's own spacing a hold later. With playout.stamped 0 (the default) nothing a client
    // shows changes and only the numbers are kept: how late lines arrive beyond the fastest transit.
    //
    // One machine or two: only differences between this client's own clock readings and the stamps are used, never
    // the two clocks compared directly.
    //
    // The hold (playout.adapt, 0 by default, only with playout.stamped 1). A pose shown a fixed hold after its stamp
    // has to have arrived by then. With the hold at 120 ms a pose more than 120 ms late was shown as a stall and then
    // a jump (the A/B on boot claude-stage0c: equal to arrival-time playout on a quiet machine, worse under load, car
    // delay band 154 / 118 ms against 56 / 57 ms). So the hold follows what is measured: the lateness 98 of 100 stamps
    // stayed under over the last 30 s, plus playout.adapt-margin-ms (40: the next pose has to be there too), never
    // under playout.hold-ms and never more than playout.adapt-max-ms over it (200). It rises at 250 ms a second and
    // falls at 20 ms a second, so what is shown slows or quickens a little instead of jumping.
    //
    // The server's game ticks (playout.sim-ticks, 0 by default). The stamp is the server's
    // wall clock, and the server's game does not run its ticks evenly on it: a frame runs none, one, two or three.
    // On boot claude-series1 a tick ran a median of 25 to 38 ms and up to 97 ms after its place on an even clock, in
    // every ride (the relay's history, 33.3 ms a tick). A car at 1 m a tick then stood still for a frame and moved
    // 2 or 3 m in the next on the server (ride 040848: 56% of its cruise samples, 8 to 18% in seven other rides), the
    // stream carried those steps with the times they were made, and a copy showed them. The clock line has carried
    // the game's tick count from the start. With the tunable at 1 a clock line's time is its tick's place on an even
    // clock, 100/3 ms a tick, laid against the stamps by the earliest tick of the last 10 to 15 s (a tick is never
    // early, so that one ran on time): two ticks run in one frame are 33 ms apart again and a tick run late is placed
    // where it belonged. A tick more than playout.sim-ticks-max-ms (150) late is a pause, a stall or a change of
    // game speed: the even clock starts again from it. At a faster game speed every tick is the earliest and the
    // stamps are used as they are. The lateness the hold follows then includes how late the server ran the tick, so
    // the hold covers it. Whole milliseconds, in integers (a double in this game keeps single precision).
    //
    // stamp.per-tick (server, 0 by default): two ticks run within 5 ms shared one clock line, so the second tick's
    // poses carried the first tick's time. With the tunable at 1 a clock line is also rung whenever the tick changed.
    internal static class LineClock
    {
        private const long BucketMs = 10000;
        private const long StaleMs = 2000;

        private static long sServerLastMs;
        internal static long ServerStamps;

        private static readonly long[] sBucketMin = NewBuckets(); // element stores: TS3 mishandles array initializers
        private static long sBucketStartMs;
        private static long sCurrentStampMs;
        private static long sMaxStampMs;
        private static long sOffsetMs;
        private static bool sHasOffset;
        private static long sLastEffectiveMs;
        private static long sStamps, sLateOver100, sLateOver250, sLateMaxMs, sLateSumMs, sStale;
        // When the latest stamp of this epoch was read, how long before it this client had read no stamp, and the longest
        // time between two stamps read (with when): lines late after a pause in reading are a delivery stall, lines
        // late with no pause are a backlog.
        private static long sLateMaxAtMs, sLateMaxGapMs, sReadGapMaxMs, sReadGapAtMs, sPrevReadMs;

        // The script collector across the longest read gap: a whole-game freeze of a client shows as a read gap (ride
        // 053339 on boot claude-outfit1: 2.5 s with the controller still receiving every 30 ms and nothing in any
        // log). Collections in the gap and a heap that fell name the collector; none names the game's native side.
        private static long sPrevGcCount = -1, sPrevHeapKb, sReadGapGc = -1, sReadGapHeapBeforeKb, sReadGapHeapAfterKb;

        // The server's game ticks (see the note above). sTickMin: the smallest (stamp - tick time) of each of the last
        // three spans of TickBucketMs. The numbers are kept whether or not the tick times are used.
        private const long TickBucketMs = 5000;
        private static readonly long[] sTickMin = NewBuckets();
        private static long sTickBucketStartMs;
        private static long sMaxRawStampMs, sMaxRawTicks;
        private static long sTickStamps, sTickUsed, sTickPullSumMs, sTickPullMaxMs, sTickPullOver33, sTickResets;
        private static long sServerLastTicks;
        private static bool sPerTick;
        private static double sEpoch;

        private const int LateBins = 8;
        private const int LateBuckets = 6;
        private const long LateBucketMs = 5000;
        private static readonly long[] sLateHist = new long[LateBins * LateBuckets];
        private static int sLateBucket;
        private static long sLateBucketStartMs;
        private static long sAdaptAtMs;
        private static long sLateP98Ms;
        private static double sAdaptMs;

        // The upper edge of a lateness bin, in ms.
        private static long BinEdge(int bin)
        {
            switch (bin)
            {
                case 0: return 20;
                case 1: return 40;
                case 2: return 60;
                case 3: return 80;
                case 4: return 120;
                case 5: return 160;
                case 6: return 250;
                default: return 400;
            }
        }

        private static void NoteLate(long late, long nowMs)
        {
            if (nowMs - sLateBucketStartMs >= LateBucketMs)
            {
                sLateBucket = (sLateBucket + 1) % LateBuckets;
                for (int i = 0; i < LateBins; i++)
                {
                    sLateHist[sLateBucket * LateBins + i] = 0;
                }

                sLateBucketStartMs = nowMs;
            }

            int bin = 0;
            while (bin < LateBins - 1 && late >= BinEdge(bin))
            {
                bin++;
            }

            sLateHist[sLateBucket * LateBins + bin]++;
            long since = nowMs - sAdaptAtMs;
            if (since < 100)
            {
                return;
            }

            sAdaptAtMs = nowMs;
            long total = 0;
            for (int i = 0; i < sLateHist.Length; i++)
            {
                total += sLateHist[i];
            }

            long need = total - total / 50; // 98 of 100
            long seen = 0;
            long p98 = BinEdge(LateBins - 1);
            for (int b = 0; b < LateBins; b++)
            {
                for (int k = 0; k < LateBuckets; k++)
                {
                    seen += sLateHist[k * LateBins + b];
                }

                if (seen >= need)
                {
                    p98 = BinEdge(b);
                    break;
                }
            }

            sLateP98Ms = p98;
            double target = p98 + Tunables.Get("playout.adapt-margin-ms", 40.0);
            double step = since > 1000 ? 1000.0 : (double)since;
            if (target > sAdaptMs)
            {
                sAdaptMs = Math.Min(target, sAdaptMs + step * 0.25);
            }
            else
            {
                sAdaptMs = Math.Max(target, sAdaptMs - step * 0.02);
            }
        }

        // The hold to use for a playout hold of baseMs: baseMs itself, or with the stamps in use and playout.adapt on
        // what the measured lateness needs (see the note above).
        internal static double Hold(double baseMs)
        {
            if (baseMs <= 0.0 || !Stamped || Tunables.Get("playout.adapt", 0.0) < 1.0)
            {
                return baseMs;
            }

            double most = baseMs + Tunables.Get("playout.adapt-max-ms", 200.0);
            double hold = sAdaptMs > most ? most : sAdaptMs;
            return hold > baseMs ? hold : baseMs;
        }

        private static long[] NewBuckets()
        {
            long[] buckets = new long[3];
            buckets[0] = long.MaxValue;
            buckets[1] = long.MaxValue;
            buckets[2] = long.MaxValue;
            return buckets;
        }

        private static long NowMs
        {
            get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }
        }

        // Server: the clock line to ring before the next line, or null when off or the last one is recent enough.
        internal static string ServerStamp()
        {
            long now = NowMs;
            long since = now - sServerLastMs;
            long ticks = -1L;
            if (since >= 0 && since < 5)
            {
                if (!sPerTick)
                {
                    return null;
                }

                ticks = ServerTicks();
                if (ticks == sServerLastTicks)
                {
                    return null;
                }
            }

            if (!ServerHostMain.IsServerRole)
            {
                return null;
            }

            sPerTick = Tunables.Get("stamp.per-tick", 0.0) >= 1.0;
            double every = Tunables.Get("stamp.every-ms", 0.0);
            if (every <= 0.0)
            {
                return null;
            }

            if (ticks < 0L)
            {
                ticks = ServerTicks();
            }

            if (since >= 0 && since < every && !(sPerTick && ticks != sServerLastTicks))
            {
                return null;
            }

            sServerLastMs = now;
            sServerLastTicks = ticks;
            ServerStamps++;
            CultureInfo c = CultureInfo.InvariantCulture;
            return "Ck|" + now.ToString(c) + "|" + ticks.ToString(c);
        }

        private static long ServerTicks()
        {
            try
            {
                return SimClock.CurrentTicks;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        // Client: the time of a clock line stamped at stamp whose game tick is ticks. The stamp itself unless
        // playout.sim-ticks is 1; then the tick's place on an even clock (see the note above), never after the stamp.
        private static long TickTime(long stamp, long ticks, long nowMs)
        {
            if (ticks <= 0L)
            {
                return stamp;
            }

            long d = stamp - ticks * 100L / 3L;
            if (nowMs - sTickBucketStartMs >= TickBucketMs)
            {
                sTickMin[2] = sTickMin[1];
                sTickMin[1] = sTickMin[0];
                sTickMin[0] = long.MaxValue;
                sTickBucketStartMs = nowMs;
            }

            long min = sTickMin[0];
            if (sTickMin[1] < min) min = sTickMin[1];
            if (sTickMin[2] < min) min = sTickMin[2];
            long most = (long)Tunables.Get("playout.sim-ticks-max-ms", 150.0);
            if (min != long.MaxValue && d - min > most)
            {
                // a pause, a stall or a slower game speed: the even clock starts again from this tick
                sTickResets++;
                sTickMin[0] = long.MaxValue;
                sTickMin[1] = long.MaxValue;
                sTickMin[2] = long.MaxValue;
                min = long.MaxValue;
            }

            if (d < sTickMin[0])
            {
                sTickMin[0] = d;
            }

            if (d < min)
            {
                min = d;
            }

            long pull = d - min;
            sTickStamps++;
            sTickPullSumMs += pull;
            if (pull > sTickPullMaxMs) sTickPullMaxMs = pull;
            if (pull > 33) sTickPullOver33++;
            if (Tunables.Get("playout.sim-ticks", 0.0) < 1.0)
            {
                return stamp;
            }

            sTickUsed++;
            return stamp - pull;
        }

        // True when the lines' own times are used for playout (playout.stamped 1 and an offset is known).
        internal static bool Stamped
        {
            get { return sHasOffset && Tunables.Get("playout.stamped", 0.0) >= 1.0; }
        }

        // Client: a clock line was read at nowMs. A stamp met a second time, or an older one, is ignored.
        internal static void Arrived(string stampText, string ticksText, long nowMs)
        {
            long stamp;
            if (stampText == null || !long.TryParse(stampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp))
            {
                return;
            }

            long ticks;
            if (ticksText == null || !long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
            {
                ticks = 0L;
            }

            // A stamp met a second time, or an older one. Two ticks of one millisecond (stamp.per-tick) are both taken.
            if (stamp < sMaxRawStampMs || (stamp == sMaxRawStampMs && ticks <= sMaxRawTicks))
            {
                return;
            }

            sMaxRawStampMs = stamp;
            sMaxRawTicks = ticks;

            double epoch = Tunables.Get("telemetry.epoch", 0.0);
            if (epoch != sEpoch)
            {
                sEpoch = epoch;
                sStamps = 0; sLateOver100 = 0; sLateOver250 = 0; sLateMaxMs = 0; sLateSumMs = 0; sStale = 0;
                sLateMaxAtMs = 0; sLateMaxGapMs = 0; sReadGapMaxMs = 0; sReadGapAtMs = 0;
                sReadGapGc = -1; sReadGapHeapBeforeKb = 0; sReadGapHeapAfterKb = 0;
                sTickStamps = 0; sTickUsed = 0; sTickPullSumMs = 0; sTickPullMaxMs = 0; sTickPullOver33 = 0; sTickResets = 0;
            }

            long readGap = sPrevReadMs == 0L ? 0L : nowMs - sPrevReadMs;
            sPrevReadMs = nowMs;
            long gcCount = -1, heapKb = 0;
            try
            {
                gcCount = GC.CollectionCount(0);
                heapKb = GC.GetTotalMemory(false) / 1024L;
            }
            catch (Exception)
            {
                gcCount = -1;
            }

            if (readGap > sReadGapMaxMs)
            {
                sReadGapMaxMs = readGap;
                sReadGapAtMs = nowMs;
                sReadGapGc = (gcCount < 0 || sPrevGcCount < 0) ? -1 : gcCount - sPrevGcCount;
                sReadGapHeapBeforeKb = sPrevHeapKb;
                sReadGapHeapAfterKb = heapKb;
            }

            sPrevGcCount = gcCount;
            sPrevHeapKb = heapKb;

            stamp = TickTime(stamp, ticks, nowMs);
            if (stamp <= sMaxStampMs)
            {
                return; // a later line of a tick already placed (or the first stamps after playout.sim-ticks went on)
            }

            sMaxStampMs = stamp;
            sCurrentStampMs = stamp;
            long delta = nowMs - stamp;
            if (nowMs - sBucketStartMs >= BucketMs)
            {
                sBucketMin[2] = sBucketMin[1];
                sBucketMin[1] = sBucketMin[0];
                sBucketMin[0] = long.MaxValue;
                sBucketStartMs = nowMs;
            }

            if (delta < sBucketMin[0])
            {
                sBucketMin[0] = delta;
            }

            long offset = sBucketMin[0];
            if (sBucketMin[1] < offset) offset = sBucketMin[1];
            if (sBucketMin[2] < offset) offset = sBucketMin[2];
            sOffsetMs = offset;
            sHasOffset = true;
            long late = delta - offset;
            NoteLate(late, nowMs);
            sStamps++;
            sLateSumMs += late;
            if (late > sLateMaxMs)
            {
                sLateMaxMs = late;
                sLateMaxAtMs = nowMs;
                sLateMaxGapMs = readGap;
            }

            if (late > 100) sLateOver100++;
            if (late > 250) sLateOver250++;
        }

        // Client: the time to count a line (or a pose) read at nowMs as having arrived. The real arrival unless the
        // stamps are in use; then the newest stamp plus the offset, never in the future and never going back.
        internal static long Effective(long nowMs)
        {
            if (sCurrentStampMs == 0 || !Stamped)
            {
                return nowMs;
            }

            long at = sCurrentStampMs + sOffsetMs;
            if (nowMs - at > StaleMs)
            {
                sStale++; // no stamp for this long: the server stopped stamping, or this client stood still
                return nowMs;
            }

            if (at > nowMs) at = nowMs;
            if (at < sLastEffectiveMs && sLastEffectiveMs <= nowMs) at = sLastEffectiveMs;
            sLastEffectiveMs = at;
            return at;
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "clock:stamped:" + (Stamped ? "1" : "0") + "/server_stamps:" + ServerStamps.ToString(c) + "/stamps:" + sStamps.ToString(c)
                + "/offset_ms:" + (sHasOffset ? sOffsetMs.ToString(c) : "-")
                + "/late_mean_ms:" + (sStamps == 0 ? "-" : (sLateSumMs / (double)sStamps).ToString("F1", c)) + "/late_max_ms:" + sLateMaxMs.ToString(c)
                + "/late_over_100:" + sLateOver100.ToString(c) + "/late_over_250:" + sLateOver250.ToString(c) + "/stale:" + sStale.ToString(c)
                + "/late_p98_ms:" + sLateP98Ms.ToString(c) + "/adapt_ms:" + sAdaptMs.ToString("F0", c)
                + "/late_max_at:" + At(sLateMaxAtMs) + "/late_max_after_ms:" + sLateMaxGapMs.ToString(c)
                + "/read_gap_max_ms:" + sReadGapMaxMs.ToString(c) + "/read_gap_at:" + At(sReadGapAtMs)
                + "/read_gap_gc:" + sReadGapGc.ToString(c) + "/read_gap_heap_kb:" + sReadGapHeapBeforeKb.ToString(c) + ">" + sReadGapHeapAfterKb.ToString(c)
                + "/tick_stamps:" + sTickStamps.ToString(c) + "/tick_used:" + sTickUsed.ToString(c)
                + "/tick_late_mean_ms:" + (sTickStamps == 0 ? "-" : (sTickPullSumMs / (double)sTickStamps).ToString("F1", c))
                + "/tick_late_max_ms:" + sTickPullMaxMs.ToString(c) + "/tick_late_over_33:" + sTickPullOver33.ToString(c)
                + "/tick_resets:" + sTickResets.ToString(c);
        }

        private static string At(long ms)
        {
            return ms == 0L ? "-" : new DateTime(ms * TimeSpan.TicksPerMillisecond).ToString("HH.mm.ss.fff", CultureInfo.InvariantCulture);
        }
    }
}
