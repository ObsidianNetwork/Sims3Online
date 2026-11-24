using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Client side: one delay for every stream line, so everything a client shows runs the same time behind the server.
    //
    // The playout buffer (TransformPlayout) shows a moving object a hold behind its newest pose (playout.hold-ms, 0 by
    // default). Every other line was applied when it arrived. A client then showed two times at once:
    // the car about 150 ms behind the server, and a Sim stepping into it or out of it about 45 ms behind
    // (two-lots-travel-stability series F, 2026-11-15: the identical 6.87 m boarding step 33 to 62 ms after the
    // server's while the ride ran 150 to 200 ms after it; motion 083932 and 083932-c2). At the get-out the buffer had
    // to bring the car to its newest pose at once so the Sim could be placed, a jump of up to a hold of travel
    // (motion 083709-c2: 10.11 m in one half-second window, 3.2 m of ride and the 6.87 m exit).
    //
    // Here the lines read from the ring wait the same hold before they are applied, in the order they arrived
    // (LineDelayQueue). Transforms of an object in motion still go to the playout buffer when they arrive, because it
    // needs them ahead of time to smooth them, and it shows them a hold later itself. So a pose and a line that
    // arrived together are shown together. A delayed line that has to follow the object (TransformPlayout.Barrier and
    // Defer) follows it up to the poses that had arrived when the line arrived, not to the newest one.
    //
    // playout.line-delay-ms sets the delay on its own; below 0 (the default) it is the playout hold, and 0 turns it
    // off while the hold stays on (the behaviour before this file). A delay shorter than the hold lets a placement
    // line pass a pose that arrived before it.
    //
    // Only the ring path waits here. A SACS_REPLAY sent over the bridge (tools, tests) is applied at once.
    internal static class LineDelay
    {
        private static readonly LineDelayQueue sQueue = new LineDelayQueue();
        private static readonly Dictionary<string, bool> sWalkersInBatch = new Dictionary<string, bool>();
        private static long sBatches, sLines, sOnArrival, sApplied, sErrors, sLateMaxMs;

        // Minimal reset hook for ResetPresentation(); Slice 2 replaces this with PresentationSession generation tracking.
        internal static int Reset()
        {
            sWalkersInBatch.Clear();
            return sQueue.Clear();
        }

        internal static double DelayMs
        {
            get
            {
                double own = Tunables.Get("playout.line-delay-ms", -1.0);
                return own >= 0.0 ? own : LineClock.Hold(Tunables.Get("playout.hold-ms", 0.0));
            }
        }

        private static long NowMs
        {
            get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }
        }

        // Lines just read from the ring. Returns them when there is no delay and nothing waits (apply now), otherwise
        // null: they wait. taken receives the lines the playout buffer took on arrival, for the received counts.
        internal static string[] Arrive(string[] lines, Converter<ulong, ObjectGuid> alias, Predicate<ulong> walking, List<string> taken)
        {
            double delay = DelayMs;
            if (lines == null || lines.Length == 0 || (delay <= 0.0 && sQueue.Waiting == 0))
            {
                return lines;
            }

            long now = NowMs;
            // When the batch counts as having arrived: now, or with playout.stamped the server's stamp plus the clocks'
            // offset (LineClock), so a batch that was slow on the way is not shown late.
            long at = LineClock.Effective(now);
            sWalkersInBatch.Clear();
            List<string> keep = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                bool took = false;
                try
                {
                    string kind = LineDelayQueue.Field(line, 1);
                    string obj = LineDelayQueue.Field(line, 2);
                    if (kind == "Ck")
                    {
                        // The server's clock line: read here, when it arrives. With the stamps in use the lines before
                        // it are their own batch, due by the stamp they followed.
                        if (LineClock.Stamped && keep.Count > 0)
                        {
                            sQueue.Add(keep.ToArray(), at, delay);
                            sBatches++;
                            sLines += keep.Count;
                            keep.Clear();
                        }

                        LineClock.Arrived(obj, LineDelayQueue.Field(line, 3), now);
                        at = LineClock.Effective(now);
                        took = true;
                    }
                    else if (kind == "Rs" && obj != null)
                    {
                        sWalkersInBatch[obj] = true;
                    }
                    else if (kind == "Ob" && obj != null && LineDelayQueue.Field(line, 3) == "SetTransform:m"
                        && !sQueue.WalkPending(obj) && !sWalkersInBatch.ContainsKey(obj))
                    {
                        // A Sim whose walk line still waits is about to walk: its transform is left to the walk.
                        string clean = WireNumbers.Clean(line);
                        took = TransformPlayout.TakeOnArrival(clean.Split('|'), alias, walking);
                        if (took)
                        {
                            MotionWatch.NoteApplied(clean);
                        }
                    }
                }
                catch (Exception)
                {
                    sErrors++;
                    took = false;
                }

                if (took)
                {
                    sOnArrival++;
                    if (taken != null)
                    {
                        taken.Add(line);
                    }
                }
                else
                {
                    keep.Add(line);
                }
            }

            if (keep.Count > 0)
            {
                sQueue.Add(keep.ToArray(), at, delay);
                sBatches++;
                sLines += keep.Count;
            }

            return null;
        }

        // Applies every batch whose time has come, oldest first.
        internal static void ApplyDue(Converter<string[], string> apply)
        {
            if (sQueue.Waiting == 0 || apply == null)
            {
                return;
            }

            long now = NowMs;
            for (int n = 0; n < 512; n++)
            {
                LineDelayQueue.Batch batch = sQueue.Next(now);
                if (batch == null)
                {
                    return;
                }

                if (now - batch.DueMs > sLateMaxMs)
                {
                    sLateMaxMs = now - batch.DueMs;
                }

                TransformPlayout.BeginDelayed(batch.ArrivedMs);
                try
                {
                    apply(batch.Lines);
                    sApplied += batch.Lines.Length;
                }
                catch (Exception)
                {
                    sErrors++;
                }
                finally
                {
                    TransformPlayout.EndDelayed();
                }
            }
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "line_delay_ms:" + DelayMs.ToString("F0", c) + "/waiting:" + sQueue.Waiting.ToString(c) + "/batches:" + sBatches.ToString(c)
                + "/lines:" + sLines.ToString(c) + "/on_arrival:" + sOnArrival.ToString(c) + "/applied:" + sApplied.ToString(c)
                + "/late_max_ms:" + sLateMaxMs.ToString(c) + "/errors:" + sErrors.ToString(c) + "/" + LineClock.Report();
        }
    }
}
