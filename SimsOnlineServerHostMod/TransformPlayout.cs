using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Client side: a playout buffer for the server's streamed object transforms (Ob|object|SetTransform:m, a car on the
    // road). The server's game moves such an object one fixed step per simulator tick and its lines leave in sends that
    // are 30 to 110 ms apart and sometimes clumped; applied on arrival the copy stalls and jumps (two-lots-travel run 19:
    // the copy stood still in half of the ride's samples). Here each object in motion is shown a little in the past
    // (playout.hold-ms) and moved every update along the received poses at their smoothed rate, so an uneven arrival
    // becomes an even ride. Off by default (hold 0): every line is then applied on arrival as before.
    //
    // Order is kept against the rest of the stream in two ways. A placement line for the object itself or for anything
    // riding in it (the get-out SetPosition) first brings the object to its newest pose, then applies. A position line of
    // a Sim riding in it (Pz) waits until the object reaches the pose that was newest when the line arrived, so the copy
    // is never pulled ahead of its seat. A line that waited is dropped when its Sim has left the object by then.
    internal static class TransformPlayout
    {
        private sealed class Pose
        {
            public Matrix44 Transform;
            public long ArrivedMs;
            public List<string> Then; // lines that wait for this pose
        }

        private sealed class Track
        {
            public ObjectGuid Object;
            public Matrix44 From;          // the pose the copy last passed (what it shows when Progress is 0)
            public long FromMs;            // when From counts as having arrived (the stamps' playout, LineClock)
            public long ShownMs;           // the stamps' playout: the time on the server's timeline shown now; 0 = not yet
            public readonly List<Pose> Queue = new List<Pose>();
            public double Progress;        // 0..1 from From to Queue[0]
            public bool Playing;
            public long LastArrivalMs;
            public readonly long[] Arrivals = new long[ArrivalWindow];
            public int ArrivalCount;
        }

        private const int ArrivalWindow = 16;
        private const int MaxQueue = 48;        // more than this waiting means playout is not keeping up: catch up at once
        private const int MaxParents = 4;
        private const double DefaultIntervalMs = 33.0;

        private static readonly Dictionary<ulong, Track> sTracks = new Dictionary<ulong, Track>();
        private static readonly List<ulong> sScratch = new List<ulong>();
        private static bool sScheduled;
        private static bool sApplyingDeferred;
        // The line delay (LineDelay.cs). sOnArrival: a transform offered when it arrived, ahead of its batch. sDelayed:
        // a batch that waited its delay is being applied, and sLineArrivedMs is when that batch arrived.
        private static bool sOnArrival;
        private static bool sDelayed;
        private static long sLineArrivedMs;
        private static long sLastUpdateMs;
        private static Converter<string[], string> sApplyLines;
        private static Converter<ulong, ObjectGuid> sAlias;

        private static long sTaken, sDirect, sPosesPlayed, sUpdates, sBarriers, sDeferred, sCatchUps, sDry, sStalled, sDropped, sErrors;
        private static long sLimited, sLimitJumps;
        private static double sLagMs, sSpeed = 1.0, sIntervalMs;

        // Minimal reset hook for ResetPresentation(); Slice 2 replaces this with PresentationSession generation/binding stamps.
        internal static void Reset()
        {
            sTracks.Clear();
            sScratch.Clear();
            sApplyingDeferred = false;
            sOnArrival = false;
            sDelayed = false;
            sLineArrivedMs = 0L;
        }

        // Milliseconds an object in motion is shown behind its newest received pose; 0 turns the buffer off.
        // With the stamps in use the hold follows the measured lateness (LineClock.Hold); the line delay uses the same.
        private static double HoldMs { get { return LineClock.Hold(Tunables.Get("playout.hold-ms", 0.0)); } }
        // A stream with no pose for this long has stopped: the next pose starts over and is applied at once.
        private static double IdleMs { get { return Tunables.Get("playout.idle-ms", 400.0); } }
        // How hard the playout rate follows the buffer's depth (1 = twice the rate when a whole hold behind).
        private static double Gain { get { return Tunables.Get("playout.gain", 1.0); } }
        // The stamps' playout: how fast the shown time may run through poses that arrived late (2 = twice real time, as
        // the arrival-time playout's own limit); under 1 the shown time jumps to where it should be, as before.
        private static double CatchUpRate { get { return Tunables.Get("playout.catchup-rate", 2.0); } }
        // Further behind than this the shown time does jump: a backlog this long is not worth playing.
        private const long CatchUpMostMs = 1500;

        private static long NowMs { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; } }

        // A transform line as it arrives, while the rest of its batch waits the line delay. True when the buffer took
        // it. The first pose of a move is queued too (it is not shown at once), so it shows a hold after it arrived like
        // the lines that arrived with it.
        internal static bool TakeOnArrival(string[] f, Converter<ulong, ObjectGuid> alias, Predicate<ulong> walking)
        {
            sOnArrival = true;
            try
            {
                return Take(f, alias, walking);
            }
            finally
            {
                sOnArrival = false;
            }
        }

        // Around the apply of a batch that waited the line delay: arrivedMs is when the batch arrived.
        internal static void BeginDelayed(long arrivedMs)
        {
            sDelayed = true;
            sLineArrivedMs = arrivedMs;
        }

        internal static void EndDelayed()
        {
            sDelayed = false;
            sLineArrivedMs = 0L;
        }

        // An Ob line. True when the buffer took it (a transform of an object in motion); false when the caller applies it
        // as before, after anything it has to follow was brought up to date.
        internal static bool Take(string[] f, Converter<ulong, ObjectGuid> alias, Predicate<ulong> walking)
        {
            double hold = HoldMs;
            if ((hold <= 0.0 && sTracks.Count == 0) || f.Length < 5)
            {
                return false;
            }

            try
            {
                ObjectGuid obj = alias(ulong.Parse(f[2], CultureInfo.InvariantCulture));
                if (!obj.IsValid)
                {
                    return false;
                }

                if (f[3] != "SetTransform:m")
                {
                    Barrier(obj); // some other placement of it, or of something riding in a buffered object
                    return false;
                }

                if (sDelayed)
                {
                    return false; // offered when it arrived (LineDelay.Arrive) and left to the caller then
                }

                Track track;
                bool known = sTracks.TryGetValue(obj.Value, out track);
                if (hold <= 0.0 || (walking != null && walking(obj.Value)))
                {
                    if (known)
                    {
                        Finish(track);
                        sTracks.Remove(obj.Value);
                    }

                    return false;
                }

                Matrix44 transform;
                if (!ParseTransform(f[4], out transform))
                {
                    if (known)
                    {
                        Finish(track);
                    }

                    return false;
                }

                long now = NowMs;
                long at = LineClock.Effective(now); // now, or with playout.stamped the pose's stamp plus the offset
                if (!known)
                {
                    track = new Track();
                    track.Object = obj;
                    sTracks[obj.Value] = track;
                }

                bool stalled = track.Queue.Count > 0 && (now - sLastUpdateMs > 250 || track.Queue.Count >= MaxQueue);
                if (!known || stalled || now - track.LastArrivalMs > IdleMs)
                {
                    // The first pose of a move (or the updates stopped coming): shown at once, the buffer starts behind it.
                    if (stalled)
                    {
                        sStalled++;
                    }

                    Finish(track);
                    track.From = transform;
                    track.FromMs = at;
                    track.ShownMs = 0L;
                    track.Progress = 0.0;
                    track.Playing = false;
                    track.ArrivalCount = 0;
                    NoteArrival(track, now);
                    sDirect++;
                    Schedule();
                    if (!sOnArrival)
                    {
                        return false;
                    }

                    // With the line delay on, this pose waits as the first of the queue: nothing is placed until it
                    // has waited the hold.
                    Pose first = new Pose();
                    first.Transform = transform;
                    first.ArrivedMs = at;
                    track.Queue.Add(first);
                    BoundaryReplay.Objects.Applied++;
                    sTaken++;
                    return true;
                }

                Pose pose = new Pose();
                pose.Transform = transform;
                pose.ArrivedMs = at;
                track.Queue.Add(pose);
                NoteArrival(track, now);
                BoundaryReplay.Objects.Applied++;
                sTaken++;
                Schedule();
                return true;
            }
            catch (Exception)
            {
                sErrors++;
                return false;
            }
        }

        // A Sim position line (Pz). True when it now waits for the object the Sim rides in.
        internal static bool Defer(string line, string[] f, Converter<ulong, ObjectGuid> alias, Converter<string[], string> applyLines)
        {
            if (sTracks.Count == 0 || sApplyingDeferred || f.Length < 3)
            {
                return false;
            }

            try
            {
                sApplyLines = applyLines;
                sAlias = alias;
                Track carrier = Carrier(alias(ulong.Parse(f[2], CultureInfo.InvariantCulture)));
                if (carrier == null || carrier.Queue.Count == 0)
                {
                    return false;
                }

                Pose newest = null;
                for (int i = carrier.Queue.Count - 1; i >= 0; i--)
                {
                    if (!sDelayed || carrier.Queue[i].ArrivedMs <= sLineArrivedMs)
                    {
                        newest = carrier.Queue[i];
                        break;
                    }
                }

                if (newest == null)
                {
                    return false; // the object has already passed every pose that had arrived when this line did
                }

                if (newest.Then == null)
                {
                    newest.Then = new List<string>();
                }

                newest.Then.Add(line);
                sDeferred++;
                return true;
            }
            catch (Exception)
            {
                sErrors++;
                return false;
            }
        }

        internal static string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "hold_ms:" + HoldMs.ToString("F0", c) + "/tracks:" + sTracks.Count.ToString(c) + "/taken:" + sTaken.ToString(c)
                + "/direct:" + sDirect.ToString(c) + "/played:" + sPosesPlayed.ToString(c) + "/updates:" + sUpdates.ToString(c)
                + "/barriers:" + sBarriers.ToString(c) + "/deferred:" + sDeferred.ToString(c) + "/catchups:" + sCatchUps.ToString(c)
                + "/dry:" + sDry.ToString(c) + "/stalled:" + sStalled.ToString(c) + "/dropped:" + sDropped.ToString(c) + "/errors:" + sErrors.ToString(c)
                + "/lag_ms:" + sLagMs.ToString("F0", c) + "/speed:" + sSpeed.ToString("F2", c) + "/interval_ms:" + sIntervalMs.ToString("F1", c)
                + "/limited:" + sLimited.ToString(c) + "/limit_jumps:" + sLimitJumps.ToString(c)
                + "/" + LineDelay.Report();
        }

        // The buffered object this one rides in (itself excluded), through its slot parents.
        private static Track Carrier(ObjectGuid obj)
        {
            ObjectGuid at = obj;
            for (int i = 0; i < MaxParents && at.IsValid; i++)
            {
                at = Slots.GetParentId(at);
                Track track;
                if (at.IsValid && sTracks.TryGetValue(at.Value, out track))
                {
                    return track;
                }
            }

            return null;
        }

        private static void Barrier(ObjectGuid obj)
        {
            Track track;
            if (!sTracks.TryGetValue(obj.Value, out track))
            {
                track = Carrier(obj);
            }

            if (track != null && track.Queue.Count > 0)
            {
                sBarriers++;
                // A line that waited the line delay follows the object only up to the poses that had arrived when the
                // line did: the newer ones are still ahead of what the client shows.
                FinishThrough(track, sDelayed ? sLineArrivedMs : long.MaxValue);
            }
        }

        // Bring the copy to its newest received pose now and release every line that waited on the way.
        private static void Finish(Track track)
        {
            FinishThrough(track, long.MaxValue);
        }

        // The same, up to the newest pose that arrived no later than arrivedBy; later poses stay queued.
        private static void FinishThrough(Track track, long arrivedBy)
        {
            int count = 0;
            while (count < track.Queue.Count && track.Queue[count].ArrivedMs <= arrivedBy)
            {
                count++;
            }

            if (count == 0)
            {
                return;
            }

            List<string> due = null;
            for (int i = 0; i < count; i++)
            {
                due = Collect(due, track.Queue[i]);
            }

            track.From = track.Queue[count - 1].Transform;
            track.FromMs = track.Queue[count - 1].ArrivedMs;
            track.Queue.RemoveRange(0, count);
            track.Progress = 0.0;
            Place(track.Object, track.From);
            Release(track, due);
        }

        private static List<string> Collect(List<string> due, Pose pose)
        {
            if (pose.Then != null)
            {
                if (due == null)
                {
                    due = new List<string>();
                }

                due.AddRange(pose.Then);
            }

            return due;
        }

        // The lines that waited for a pose this object has now reached. Each is a seat position of a Sim that rode in
        // the object when the line arrived. A Sim that has left it since (the slot detach of a get-out is applied on
        // arrival, while the object is still a hold behind) is no longer where that line says: applied, the line put
        // the copy back at the taxi's seat 6.9 m from where the get-out had just placed it, as a "mirror posture snap"
        // (two-lots-travel-stability with playout.hold-ms 120, 2026-11-15: motion 061417-c2 at 19:14:44.464 and
        // 062012). Such a line is dropped.
        private static void Release(Track track, List<string> due)
        {
            if (due == null || sApplyLines == null)
            {
                return;
            }

            for (int i = due.Count - 1; i >= 0; i--)
            {
                if (!StillRides(due[i], track))
                {
                    due.RemoveAt(i);
                    sDropped++;
                }
            }

            if (due.Count == 0)
            {
                return;
            }

            bool before = sApplyingDeferred;
            sApplyingDeferred = true;
            try
            {
                sApplyLines(due.ToArray());
            }
            catch (Exception)
            {
                sErrors++;
            }

            sApplyingDeferred = before;
        }

        private static bool StillRides(string line, Track track)
        {
            try
            {
                string[] f = line.Split('|');
                return sAlias == null || f.Length < 3 || Carrier(sAlias(ulong.Parse(f[2], CultureInfo.InvariantCulture))) == track;
            }
            catch (Exception)
            {
                sErrors++;
                return true;
            }
        }

        private static void NoteArrival(Track track, long now)
        {
            track.LastArrivalMs = now;
            if (track.ArrivalCount < ArrivalWindow)
            {
                track.Arrivals[track.ArrivalCount++] = now;
                return;
            }

            Array.Copy(track.Arrivals, 1, track.Arrivals, 0, ArrivalWindow - 1);
            track.Arrivals[ArrivalWindow - 1] = now;
        }

        // The mean time between this object's recent poses: clumps and gaps average out over the window.
        private static double Interval(Track track)
        {
            if (track.ArrivalCount < 3)
            {
                return DefaultIntervalMs;
            }

            double mean = (track.Arrivals[track.ArrivalCount - 1] - track.Arrivals[0]) / (double)(track.ArrivalCount - 1);
            return Math.Max(8.0, Math.Min(mean, 250.0));
        }

        private static void Schedule()
        {
            if (!sScheduled)
            {
                sScheduled = true;
                sLastUpdateMs = NowMs;
                Simulator.AddObject(new PlayoutTask());
            }
        }

        // One simulator update: every buffered object moves on. False when nothing is left to move.
        private static bool Update()
        {
            long now = NowMs;
            double dt = Math.Max(0.0, Math.Min(now - sLastUpdateMs, 100.0));
            sLastUpdateMs = now;
            sUpdates++;
            double hold = HoldMs;
            double idle = IdleMs;
            sScratch.Clear();
            sScratch.AddRange(sTracks.Keys);
            for (int i = 0; i < sScratch.Count; i++)
            {
                Track track;
                if (!sTracks.TryGetValue(sScratch[i], out track))
                {
                    continue;
                }

                if (hold <= 0.0)
                {
                    Finish(track);
                    sTracks.Remove(sScratch[i]);
                    continue;
                }

                if (track.Queue.Count == 0)
                {
                    if (now - track.LastArrivalMs > idle)
                    {
                        sTracks.Remove(sScratch[i]);
                    }
                    else if (track.Playing && dt > 0.0)
                    {
                        sDry++;
                    }

                    continue;
                }

                if (track.Queue.Count >= MaxQueue)
                {
                    sCatchUps++;
                    Finish(track);
                    continue;
                }

                if (!track.Playing)
                {
                    if (now - track.Queue[0].ArrivedMs < hold)
                    {
                        continue;
                    }

                    track.Playing = true;
                }

                if (LineClock.Stamped)
                {
                    // The stamps' playout: the object is shown where the server had it a hold ago. Each pose counts as
                    // having arrived at its stamp plus the clocks' offset, so the poses keep the server's own spacing
                    // however they arrived, and no rate is guessed.
                    // Whole milliseconds, in integers: a double in this game keeps single precision, and a timestamp
                    // of 6e13 ms rounds to a multiple of about 70 minutes in it (boot claude-stage0b: with "now - hold"
                    // in doubles the buffer took 1,429 poses and played none). Only differences become doubles.
                    //
                    // A pose later than the hold was a stall and then a jump to where the object should be by then
                    // (rides fs-2 and fs-3 on boot claude-follow1, another project's build running: slides of 2.9 to
                    // 21.6 m on both clients). So the shown time is the track's own: it follows now minus the hold, and
                    // when it is behind (poses came late, or this client stood still for a frame) it runs at up to
                    // playout.catchup-rate times real time until it is back. When the poses run out it stays at the last
                    // one's stamp, so what comes next is played from there and nothing is skipped.
                    long target = now - (long)hold;
                    long shown = target;
                    double rate = CatchUpRate;
                    if (rate >= 1.0 && track.ShownMs != 0L)
                    {
                        long from = track.ShownMs < track.FromMs ? track.FromMs : track.ShownMs;
                        long most = from + (long)(dt * rate) + 1L;
                        if (target - from > CatchUpMostMs)
                        {
                            sLimitJumps++;
                        }
                        else if (most < target)
                        {
                            shown = most;
                            sLimited++;
                        }
                    }

                    track.ShownMs = shown;
                    List<string> reachedLines = null;
                    while (track.Queue.Count > 0 && shown >= track.Queue[0].ArrivedMs)
                    {
                        Pose passed = track.Queue[0];
                        track.Queue.RemoveAt(0);
                        track.From = passed.Transform;
                        track.FromMs = passed.ArrivedMs;
                        reachedLines = Collect(reachedLines, passed);
                        sPosesPlayed++;
                    }

                    if (track.Queue.Count == 0)
                    {
                        track.Progress = 0.0;
                        track.ShownMs = track.FromMs;
                        Place(track.Object, track.From);
                    }
                    else
                    {
                        long span = track.Queue[0].ArrivedMs - track.FromMs;
                        long into = shown - track.FromMs;
                        track.Progress = span <= 0L || into <= 0L ? 0.0 : (into >= span ? 1.0 : (double)into / (double)span);
                        sIntervalMs = span;
                        Place(track.Object, Between(track.From, track.Queue[0].Transform, (float)track.Progress));
                    }

                    sLagMs = hold + (double)(target - shown);
                    sSpeed = shown < target ? rate : 1.0;
                    Release(track, reachedLines);
                    continue;
                }

                double interval = Interval(track);
                // How far behind its arrival the shown point is; the rate leans towards keeping that at the hold.
                double lag = (now - track.Queue[0].ArrivedMs) + (1.0 - track.Progress) * interval;
                double speed = Math.Max(0.5, Math.Min(1.0 + Gain * (lag - hold) / hold, 2.0));
                sLagMs = lag;
                sSpeed = speed;
                sIntervalMs = interval;
                track.Progress += dt * speed / interval;
                List<string> due = null;
                while (track.Progress >= 1.0 && track.Queue.Count > 0)
                {
                    Pose reached = track.Queue[0];
                    track.Queue.RemoveAt(0);
                    track.From = reached.Transform;
                    track.FromMs = reached.ArrivedMs;
                    track.Progress -= 1.0;
                    due = Collect(due, reached);
                    sPosesPlayed++;
                }

                if (track.Queue.Count == 0)
                {
                    track.Progress = 0.0;
                    Place(track.Object, track.From);
                }
                else
                {
                    Place(track.Object, Between(track.From, track.Queue[0].Transform, (float)track.Progress));
                }

                Release(track, due);
            }

            return sTracks.Count > 0;
        }

        private static void Place(ObjectGuid obj, Matrix44 transform)
        {
            BoundaryReplay.Target target = BoundaryReplay.Objects;
            if (target.Instance == null && target.Holder != null)
            {
                target.Instance = target.Holder.GetValue(null);
            }

            IObjects objects = target.Instance as IObjects;
            if (objects == null)
            {
                return;
            }

            string context = SacsRecorder.LocalContext;
            SacsRecorder.LocalContext = "server placement (Ob)";
            try
            {
                objects.SetTransform(obj, transform);
                TransformGuard.After(objects, obj, transform); // a facing the game left invalid (TransformGuard.cs)
            }
            catch (Exception)
            {
                sErrors++;
            }

            SacsRecorder.LocalContext = context;
        }

        private static bool ParseTransform(string text, out Matrix44 transform)
        {
            transform = new Matrix44();
            string[] p = text.Split('~');
            if (p.Length < 16)
            {
                return false;
            }

            float[] v = new float[16];
            for (int i = 0; i < 16; i++)
            {
                v[i] = float.Parse(p[i], CultureInfo.InvariantCulture);
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i]))
                {
                    return false;
                }
            }

            transform = new Matrix44(new Vector4(v[0], v[1], v[2], v[3]), new Vector4(v[4], v[5], v[6], v[7]),
                new Vector4(v[8], v[9], v[10], v[11]), new Vector4(v[12], v[13], v[14], v[15]));
            return true;
        }

        // The pose a fraction t of the way from a to b: the place in a straight line, each axis turned towards b's and
        // kept at its own length (a plain mix of two rotations would shrink the object mid-turn).
        private static Matrix44 Between(Matrix44 a, Matrix44 b, float t)
        {
            if (t <= 0f)
            {
                return a;
            }

            if (t >= 1f)
            {
                return b;
            }

            return new Matrix44(Axis(a.right, b.right, t), Axis(a.up, b.up, t), Axis(a.at, b.at, t),
                new Vector4(a.pos.x + (b.pos.x - a.pos.x) * t, a.pos.y + (b.pos.y - a.pos.y) * t, a.pos.z + (b.pos.z - a.pos.z) * t, b.pos.w));
        }

        private static Vector4 Axis(Vector4 a, Vector4 b, float t)
        {
            float x = a.x + (b.x - a.x) * t;
            float y = a.y + (b.y - a.y) * t;
            float z = a.z + (b.z - a.z) * t;
            double mixed = Math.Sqrt(x * x + y * y + z * z);
            double from = Math.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);
            double to = Math.Sqrt(b.x * b.x + b.y * b.y + b.z * b.z);
            if (mixed < 1e-6 || double.IsNaN(mixed))
            {
                return b; // opposite axes (a half turn in one step): no direction between them, take the new one
            }

            float scale = (float)((from + (to - from) * t) / mixed);
            return new Vector4(x * scale, y * scale, z * scale, b.w);
        }

        private sealed class PlayoutTask : Sims3.SimIFace.Task
        {
            public override void Simulate()
            {
                bool keep;
                try
                {
                    keep = Update();
                }
                catch (Exception)
                {
                    sErrors++;
                    keep = false;
                    try
                    {
                        sScratch.Clear();
                        sScratch.AddRange(sTracks.Keys);
                        for (int i = 0; i < sScratch.Count; i++)
                        {
                            Track track;
                            if (sTracks.TryGetValue(sScratch[i], out track))
                            {
                                Finish(track);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Teardown best-effort after an Update failure already counted in sErrors above.
                    }

                    sTracks.Clear();
                }

                if (!keep)
                {
                    sScheduled = false;
                    Simulator.DestroyObject(ObjectId);
                }
            }
        }
    }
}
