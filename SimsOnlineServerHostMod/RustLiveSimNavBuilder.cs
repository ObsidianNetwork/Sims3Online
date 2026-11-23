using System;
using System.Collections.Generic;
using System.Globalization;
#if !CARD55_PURE
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
#endif

namespace SimsOnlineServerHost
{
    // PURE measured-input model and SIMNAV01 encoder. Geometry is never serialized to a file.
    public sealed class RustMeasuredSimNav
    {
        public sealed class Surface
        {
            public uint Id, RoomIndex;
            public ulong Lot, Room;
            public short Level;
            public int[][] Vertices;
            public int[] Center;
        }
        private sealed class Crossing
        {
            internal Surface A, B;
            internal uint Path, Entry, Exit;
            internal ulong Object;
            internal int[] JambA, JambB;
            internal int Y;
            internal bool Locked, Allowed;
        }
        private readonly ulong mLot;
        public readonly List<Surface> Cells = new List<Surface>();
        private readonly List<Crossing> mPortals = new List<Crossing>();
        private readonly List<int[][]> mTemplates = new List<int[][]>();
        private readonly List<RustPrivateNavigation.Obstacle> mObstacles = new List<RustPrivateNavigation.Obstacle>();
        public int PortalCount { get { return mPortals.Count; } }
        public ulong ContentHash;
        public ulong LotId { get { return mLot; } }
        public RustMeasuredSimNav(ulong lot) { mLot = lot; }
        private static long Cross(int[] a, int[] b, int[] c)
        {
            return ((long)b[0] - a[0]) * ((long)c[2] - a[2]) - ((long)b[2] - a[2]) * ((long)c[0] - a[0]);
        }
        public Surface AddSurface(ulong lot, ulong room, short level, int[][] vertices)
        {
            if (vertices == null || vertices.Length < 3 || vertices.Length > 8 || Cells.Count >= 65535)
                throw new FormatException("surface bounds");
            foreach (int[] vertex in vertices)
                if (vertex == null || vertex.Length != 3) throw new FormatException("surface xyz");
            int first = 0;
            long area = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                int[] v = vertices[i], w = vertices[(i + 1) % vertices.Length];
                area += (long)v[0] * w[2] - (long)v[2] * w[0];
                if (v[0] < vertices[first][0] || (v[0] == vertices[first][0] && v[2] < vertices[first][2])) first = i;
            }
            if (area == 0) throw new FormatException("surface degenerate");
            int[][] canonical = new int[vertices.Length][];
            long[] sum = new long[3];
            for (int i = 0; i < canonical.Length; i++)
            {
                int index = (first + (area < 0 ? i : vertices.Length - i)) % vertices.Length;
                canonical[i] = (int[])vertices[index].Clone();
                for (int j = 0; j < 3; j++) sum[j] += canonical[i][j];
            }
            for (int i = 0; i < canonical.Length; i++)
                if (Cross(canonical[i], canonical[(i + 1) % canonical.Length], canonical[(i + 2) % canonical.Length]) >= 0)
                    throw new FormatException("surface convexity");
            Surface s = new Surface(); s.Lot = lot; s.Room = room; s.Level = level; s.Vertices = canonical;
            s.Center = new int[] { checked((int)(sum[0] / canonical.Length)), checked((int)(sum[1] / canonical.Length)), checked((int)(sum[2] / canonical.Length)) };
            foreach (int[] v in canonical) if (!OnPlane(s, v)) throw new FormatException("surface nonplanar");
            if (!OnPlane(s, s.Center)) throw new FormatException("centroid nonplanar");
            Cells.Add(s); return s;
        }
        private static bool OnPlane(Surface s, int[] p)
        {
            int[] a = s.Vertices[0], b = s.Vertices[1], c = s.Vertices[2];
            long ux = (long)b[0] - a[0], uy = (long)b[1] - a[1], uz = (long)b[2] - a[2];
            long vx = (long)c[0] - a[0], vy = (long)c[1] - a[1], vz = (long)c[2] - a[2];
            return (uy * vz - uz * vy) * ((long)p[0] - a[0]) +
                (uz * vx - ux * vz) * ((long)p[1] - a[1]) + (ux * vy - uy * vx) * ((long)p[2] - a[2]) == 0;
        }
        public static bool Contains(Surface s, int[] point)
        {
            if (!OnPlane(s, point)) return false;
            for (int i = 0; i < s.Vertices.Length; i++)
                if (Cross(s.Vertices[i], s.Vertices[(i + 1) % s.Vertices.Length], point) > 0) return false;
            return true;
        }
        private static long Distance(int[] a, int[] b)
        {
            long x = (long)a[0] - b[0], y = (long)a[1] - b[1], z = (long)a[2] - b[2];
            return x * x + y * y + z * z;
        }
        public static bool SlotZeroIsEntry(int[] zero, int[] one, int[] cellA)
        {
            return Distance(zero, cellA) <= Distance(one, cellA);
        }
        public static bool SlotZeroIsEntry(int[] zero, int[] one, Surface cellA)
        {
            return Contains(cellA, zero) || SlotZeroIsEntry(zero, one, cellA.Center);
        }
        public int AddPortal(Surface a, Surface b, uint path, ulong obj, uint entry, uint exit,
            int[] jambA, int[] jambB, int y, bool locked, bool allowed)
        {
            if (ReferenceEquals(a, b) || mPortals.Count >= 65535 || jambA.Length != 2 || jambB.Length != 2 ||
                ((path & 0x80000000u) != 0 && obj == 0)) throw new FormatException("portal");
            if (a.Lot != b.Lot)
                foreach (int[] jamb in new int[][] { jambA, jambB })
                    if (!Contains(a, new int[] { jamb[0], y, jamb[1] }) || !Contains(b, new int[] { jamb[0], y, jamb[1] }))
                        throw new FormatException("cross-lot jamb");
            Crossing p = new Crossing(); p.A = a; p.B = b; p.Path = path; p.Object = obj;
            p.Entry = entry; p.Exit = exit; p.JambA = (int[])jambA.Clone(); p.JambB = (int[])jambB.Clone();
            p.Y = y; p.Locked = locked; p.Allowed = allowed;
            mPortals.Add(p); return mPortals.Count - 1;
        }
        public void AddObstacle(ulong obj, int[] origin, int[] min, int[] max)
        {
            for (int i = 0; i < 3; i++) if (min[i] > max[i]) throw new FormatException("obstacle bounds");
            RustPrivateNavigation.Obstacle obstacle = new RustPrivateNavigation.Obstacle();
            obstacle.Object = obj; obstacle.Template = (uint)mTemplates.Count; obstacle.Origin = (int[])origin.Clone(); obstacle.Scale = 1000;
            mTemplates.Add(new int[][] { (int[])min.Clone(), (int[])max.Clone() }); mObstacles.Add(obstacle);
        }
        internal void Order()
        {
            Cells.Sort(delegate(Surface a, Surface b)
            {
                int c = a.Lot.CompareTo(b.Lot); if (c != 0) return c;
                c = a.Level.CompareTo(b.Level); if (c != 0) return c;
                c = a.Room.CompareTo(b.Room); if (c != 0) return c;
                c = a.Center[0].CompareTo(b.Center[0]); if (c != 0) return c;
                return a.Center[2].CompareTo(b.Center[2]);
            });
            uint room = 0;
            for (int i = 0; i < Cells.Count; i++)
            {
                if (i > 0 && (Cells[i].Lot != Cells[i - 1].Lot || Cells[i].Room != Cells[i - 1].Room || Cells[i].Level != Cells[i - 1].Level)) room++;
                Cells[i].Id = (uint)i; Cells[i].RoomIndex = room;
            }
        }
        public RustPrivateNavigation.Bindings Bind(ulong epoch, ulong generation, RustPrivateNavigation.Actor[] actors)
        {
            Order();
            RustPrivateNavigation.Bindings bindings = new RustPrivateNavigation.Bindings();
            bindings.Epoch = epoch; bindings.Generation = generation; bindings.Provenance = 0; bindings.Actors = actors;
            bindings.Cells = new RustPrivateNavigation.Cell[Cells.Count];
            foreach (Surface s in Cells)
            {
                RustPrivateNavigation.Cell c = new RustPrivateNavigation.Cell();
                c.Id = s.Id; c.Lot = s.Lot; c.Room = s.Room; c.Level = s.Level; bindings.Cells[(int)s.Id] = c;
            }
            bindings.Portals = new RustPrivateNavigation.Portal[mPortals.Count];
            List<ushort> keys = new List<ushort>();
            for (int i = 0; i < mPortals.Count; i++)
            {
                Crossing crossing = mPortals[i]; bool forward = crossing.A.Id < crossing.B.Id;
                RustPrivateNavigation.Portal p = new RustPrivateNavigation.Portal(); p.Id = (uint)i; p.Object = crossing.Object;
                p.EntrySlot = forward ? crossing.Entry : crossing.Exit; p.ExitSlot = forward ? crossing.Exit : crossing.Entry;
                p.CrossLot = crossing.A.Lot != crossing.B.Lot; bindings.Portals[i] = p;
                if (crossing.Locked && crossing.Allowed) keys.Add(checked((ushort)(i + 1)));
            }
            bindings.Keys = keys.ToArray(); bindings.Obstacles = mObstacles.ToArray(); return bindings;
        }
        public byte[] Encode()
        {
            if (Cells.Count == 0) throw new FormatException("empty surfaces");
            Order();
            List<Surface> rooms = new List<Surface>();
            int vertices = 0; bool multiLot = false;
            foreach (Surface s in Cells)
            {
                vertices += s.Vertices.Length; multiLot |= s.Lot != mLot;
                if (rooms.Count == s.RoomIndex) rooms.Add(s);
            }
            if (vertices > 65535) throw new FormatException("vertex capacity");
            RustPrivateNavigation.Writer w = new RustPrivateNavigation.Writer(RustPrivateNavigation.MaxSimNav);
            w.Bytes(new byte[] { 83, 73, 77, 78, 65, 86, 48, 49 }); w.U32(1);
            w.U32(4u | (multiLot ? 2u : 0u) | (mTemplates.Count > 0 ? 1u : 0u));
            w.U64(mLot); w.U64(0); w.U32((uint)rooms.Count); w.U32((uint)Cells.Count); w.U32((uint)mPortals.Count);
            w.U32((uint)mTemplates.Count); w.U32((uint)vertices); w.U32(0); w.U64(0);
            foreach (Surface r in rooms)
            {
                int length = 0; foreach (Surface s in Cells) if (s.RoomIndex == r.RoomIndex) length++;
                w.U32(r.RoomIndex); w.U16(unchecked((ushort)r.Level)); w.U16(0); w.U16((ushort)mPortals.Count);
                w.U16(checked((ushort)r.Id)); w.U16(checked((ushort)length)); w.U16(0);
            }
            int offset = 0;
            foreach (Surface s in Cells)
            {
                w.U32(s.Id); w.U32(s.RoomIndex); w.U16(checked((ushort)offset)); w.U16((ushort)s.Vertices.Length);
                w.U16(0); w.U16((ushort)mPortals.Count); w.Xyz(s.Center); w.U32(0); offset += s.Vertices.Length;
            }
            for (int i = 0; i < mPortals.Count; i++)
            {
                Crossing p = mPortals[i];
                long dx = (long)p.JambA[0] - p.JambB[0], dz = (long)p.JambA[1] - p.JambB[1];
                int width = (int)Math.Floor(Math.Sqrt(dx * dx + dz * dz) + 0.5);
                if (width < 1 || width > 65535) throw new FormatException("portal width");
                w.U32((uint)i); w.U32(Math.Min(p.A.Id, p.B.Id)); w.U32(Math.Max(p.A.Id, p.B.Id)); w.U32(p.Path); w.U64(p.Object);
                w.I32(p.JambA[0]); w.I32(p.JambA[1]); w.I32(p.JambB[0]); w.I32(p.JambB[1]); w.I32(p.Y);
                w.U16((ushort)width); w.U16(p.Locked ? checked((ushort)(i + 1)) : (ushort)0);
            }
            for (int i = 0; i < mTemplates.Count; i++)
            { w.U32((uint)i); w.Xyz(mTemplates[i][0]); w.Xyz(mTemplates[i][1]); w.U32(1000); }
            foreach (Surface s in Cells) foreach (int[] vertex in s.Vertices) w.Xyz(vertex);
            byte[] bytes = w.Done(); ulong hash = 0xcbf29ce484222325uL;
            foreach (byte b in bytes) hash = unchecked((hash ^ b) * 0x100000001b3uL);
            for (int i = 0; i < 8; i++) bytes[56 + i] = (byte)(hash >> (i * 8));
            ContentHash = hash; return bytes;
        }
    }

#if !CARD55_PURE
    // Engine adapter at RouteCtor and the trusted memory bridge; nothing here opens a file.
    internal static class RustLiveSimNavBuilder
    {
        internal static readonly RustPrivateNavigation.SyncStore Sync =
            new RustPrivateNavigation.SyncStore(new RustPrivateNavigation.SyncStore.PlanPurge(DiscardPlans));
        internal static RustRoutePlan.LineSender Sender;
        private static RustMeasuredSimNav sSnapshot;
        private static byte[] sSimNav;
        private static RustPrivateNavigation.Bindings sBindings;
        private static ulong sInstallId, sReferenceActor;
        private static bool sCapturing, sReady;
        private static string sState = "uninstalled";
        private sealed class Pending
        {
            internal ulong WaitCorr;
            internal RustPrivateNavigation.Request Request;
            internal RustPrivateNavigation.Envelope Envelope;
            internal RouteRecorder Route;
            internal bool Committed;
            internal bool Started;
        }
        private static readonly Dictionary<ulong, Pending> sPlans = new Dictionary<ulong, Pending>();
        internal static bool Active { get { return Sync.Synced; } }
        internal static void Reset()
        {
            List<Pending> plans = new List<Pending>(sPlans.Values); sPlans.Clear();
            foreach (Pending p in plans) if (p.Route != null) p.Route.RevokePrivatePlan();
            RustBridgeWait.Cancel("private-navigation");
            Sync.ResetAttachment(); sSnapshot = null; sSimNav = null; sBindings = null;
            sInstallId = 0; sReady = false; sReferenceActor = 0; sCapturing = false; sState = "uninstalled";
        }
        internal static string AcceptSync(ulong epoch, uint index, uint count, string entries)
        {
            if (Sender == null) throw new FormatException("rust-route-on required");
            ulong previous = Sync.Epoch;
            string ack = Sync.AcceptPage(epoch, index, count, entries);
            if (Sync.Epoch != previous)
            {
                sReady = false; sInstallId = 0; sState = "invalidating";
                sSnapshot = null; sSimNav = null; sBindings = null; sReferenceActor = 0;
                List<ulong> ids = new List<ulong>(sPlans.Keys);
                foreach (ulong id in ids) Revoke(id);
                RustPrivateNavigation.Send(Sync.BeginInvalidate(), Sender);
            }
            return ack;
        }
        private static void DiscardPlans()
        {
            sReady = false; sInstallId = 0; sState = "invalidating";
            List<ulong> ids = new List<ulong>(sPlans.Keys);
            foreach (ulong id in ids) Revoke(id);
            SacsRecorder.DiscardPrivatePlans();
            ServerHostMain.DiscardNavigationBlobs();
        }
        internal static string BeginCapture(ulong lot, ulong actor, string bridgeCorr)
        {
            RustPrivateNavigation.SyncStore.Claim selected = Sync.SelectActor(actor);
            if (selected == null) return "rust-simnav-install=refused;reason=" + (actor == 0 ? "no-claimed-actor" : "unknown-actor");
            if (!Sync.Fenced) return "rust-simnav-install=refused;reason=attachment-not-fenced";
            if (sCapturing || sInstallId != 0) return "rust-simnav-install=refused;reason=install-in-progress";
            sCapturing = true; sReady = false; sState = "capturing";
            Simulator.AddObject(new OneShotFunctionTask(new Function(new CaptureTask(lot, selected.Wire, bridgeCorr).Run)));
            return null; // CaptureTask replies after capture, without waiting on the actor that must process Invalidate.
        }
        private sealed class CaptureTask
        {
            private readonly ulong mLot, mActor;
            private readonly string mBridgeCorr;
            internal CaptureTask(ulong lot, ulong actor, string corr) { mLot = lot; mActor = actor; mBridgeCorr = corr; }
            internal void Run()
            {
                string response;
                ulong epoch = Sync.Epoch;
                try
                {
                    sSnapshot = Capture(mLot, mActor);
                    if (epoch != Sync.Epoch || !Sync.Fenced || Sync.SelectActor(mActor) == null) throw new FormatException("capture ownership changed");
                    sSimNav = sSnapshot.Encode(); sReferenceActor = mActor; sBindings = null;
                    SendInstall();
                    response = InstallMetadata();
                }
                catch (Exception e)
                {
                    if (RustRoutePlan.MustRethrow(e)) throw;
                    sState = "refused"; sReady = false;
                    response = "rust-simnav-install=refused;reason=" + (e is FormatException ? e.Message : e.GetType().Name);
                }
                finally { sCapturing = false; }
                ServerHostMain.NavigationReply(mBridgeCorr, response);
            }
        }
        private static string InstallMetadata()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            RustPrivateNavigation.SyncStore.Claim actor = Sync.SelectActor(sReferenceActor);
            return "rust-simnav-install=accepted;lot=" + sSnapshot.LotId.ToString(c) + ";actor=" + sReferenceActor.ToString(c) +
                ";kernel_sim=" + actor.Kernel.ToString(c) + ";epoch=" + Sync.Epoch.ToString(c) +
                ";generation=" + Sync.Generation.ToString(c) +
                ";content_hash=" + sSnapshot.ContentHash.ToString("x16", c) + ";cells=" + sSnapshot.Cells.Count.ToString(c) +
                ";portals=" + sSnapshot.PortalCount.ToString(c) + ";keys_held=" + sBindings.Keys.Length.ToString(c) +
                ";actors=" + Sync.KnownActors().Length.ToString(c) + ";provenance=measured;storage=memory";
        }
        internal static string Status()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "rust-navigation-status=" + sState + ";epoch=" + Sync.Epoch.ToString(c) + ";generation=" + Sync.Generation.ToString(c) +
                ";installed=" + (sReady ? "1" : "0") + ";mapping_checked=" + (Sync.Synced ? "1" : "0") +
                (sSnapshot == null ? "" : ";content_hash=" + sSnapshot.ContentHash.ToString("x16", c) +
                    ";cells=" + sSnapshot.Cells.Count.ToString(c) + ";portals=" + sSnapshot.PortalCount.ToString(c) +
                    ";provenance=measured;storage=memory;surfaces_measured=1;portals_measured=1;obstacles_and_locks_measured=1;cross_lot_measured=1");
        }
        internal static void StopUnsupportedStair(string reason)
        {
            sReady = false; sState = "NEEDS-USER;reason=" + reason;
        }
        private static void SendInstall()
        {
            sReady = false; sState = "installing";
            sBindings = sSnapshot.Bind(Sync.Epoch, Sync.NextInstallGeneration(), Sync.KnownActors());
            RustPrivateNavigation.Frame frame = new RustPrivateNavigation.Frame();
            frame.Operation = 3; frame.Id = Sync.NextFrameId(); frame.Bindings = sBindings; frame.Data = sSimNav;
            sInstallId = frame.Id; RustPrivateNavigation.Send(frame, Sender);
        }
        internal static string Result(byte[] bytes)
        {
            RustPrivateNavigation.Frame frame = RustPrivateNavigation.Decode(bytes);
            string ack = RustPrivateNavigation.ResultAcknowledgement(bytes);
            if (frame.Operation == 7) { Revoke(frame.Id); return ack; }
            if (frame.OriginalOperation == 4)
            {
                RustPrivateNavigation.Frame retry = Sync.ApplyInvalidateResult(frame);
                if (retry != null) RustPrivateNavigation.Send(retry, Sender);
                else sState = "uninstalled";
                return ack;
            }
            if (frame.OriginalOperation == 3)
            {
                if (frame.Id != sInstallId || frame.Epoch != Sync.Epoch || frame.Generation != Sync.Generation)
                    throw new FormatException("install correlation");
                sInstallId = 0; sReady = frame.Status == 1; sState = sReady ? "ready" : "refused";
                return ack;
            }
            Pending p;
            if (!sPlans.TryGetValue(frame.Id, out p)) throw new FormatException("unknown navigation request");
            if (frame.OriginalOperation != 5 || frame.Epoch != p.Request.Epoch || frame.Generation != p.Request.Generation)
                throw new FormatException("plan correlation");
            if (frame.Status == 0)
            {
                if (p.Committed) throw new FormatException("duplicate staged plan");
                p.Committed = true;
                RustPrivateNavigation.Frame commit = new RustPrivateNavigation.Frame(); commit.Id = frame.Id; commit.Operation = 6;
                RustPrivateNavigation.Send(commit, Sender);
            }
            else
            {
                if (frame.Status == 1) throw new FormatException("offer requires named blob");
                RustBridgeWait.Offer("private-navigation", p.WaitCorr, null, "refused");
                sPlans.Remove(frame.Id);
            }
            return ack;
        }
        internal static string Offer(string blob, ulong id, ulong epoch, ulong generation, ulong corr, ulong actor, byte[] bytes)
        {
            Pending p;
            RustPrivateNavigation.Envelope envelope = RustPrivateNavigation.DecodeEnvelope(bytes);
            if (!sPlans.TryGetValue(id, out p) || !p.Committed || envelope.Epoch != epoch || envelope.Generation != generation ||
                envelope.Correlation != corr || envelope.Actor != actor || epoch != p.Request.Epoch ||
                generation != p.Request.Generation || corr != p.Request.Correlation || actor != p.Request.Actor)
                throw new FormatException("offer identity");
            if (!Same(envelope.Start, p.Request.Start) || !Same(envelope.Goal, p.Request.Goal))
                throw new FormatException("offer endpoints");
            string ack = RustPrivateNavigation.OfferAcknowledgement(blob, id, epoch, generation, corr, actor);
            if (!RustBridgeWait.Offer("private-navigation", p.WaitCorr, envelope, null)) throw new FormatException("offer expired");
            p.Envelope = envelope; return ack;
        }
        private static bool Same(int[] a, int[] b) { return a[0] == b[0] && a[1] == b[1] && a[2] == b[2]; }
        internal static RustPrivateNavigation.Envelope RequestAndWait(ulong actor, Vector3 start, Vector3 goal, out ulong requestId)
        {
            requestId = 0;
            if (!sReady || Sender == null || RustRoutePlan.InBridgePump || !RouteRecorder.YieldEnv.Instance.CanYield) return null;
            uint kernel = Sync.KernelFor(actor);
            if (kernel == 0) return null;
            bool registered = false;
            foreach (RustPrivateNavigation.Actor a in sBindings.Actors) if (a.Wire == actor && a.Kernel == kernel) registered = true;
            if (!registered || sReferenceActor != actor)
            {
                if (sCapturing || sInstallId != 0) return null;
                if (sReferenceActor != actor)
                {
                    sSnapshot = Capture(sSnapshot.LotId, actor);
                    sSimNav = sSnapshot.Encode(); sReferenceActor = actor;
                }
                SendInstall();
                DateTime deadline = DateTime.UtcNow.AddSeconds(0.75);
                while (!sReady && sInstallId != 0 && DateTime.UtcNow < deadline) Simulator.Sleep(0);
                if (!sReady) return null;
            }
            RustPrivateNavigation.SyncStore.Claim claim = Sync.SelectActor(actor);
            RustPrivateNavigation.Request request = new RustPrivateNavigation.Request();
            request.Epoch = Sync.Epoch; request.Generation = Sync.Generation; request.Correlation = Sync.NextPlanCorr();
            request.Actor = actor; request.Kernel = kernel; request.Start = Mm(start); request.Goal = Mm(goal);
            request.StartIdentity = Identity(start); request.GoalIdentity = Identity(goal);
            ulong id = Sync.NextFrameId();
            Pending pending = new Pending(); pending.Request = request; sPlans.Add(id, pending);
            string reason;
            bool retained = false;
            try
            {
                RustPrivateNavigation.Envelope result = RustBridgeWait.Request("private-navigation",
                    delegate(ulong waitCorr)
                    {
                        pending.WaitCorr = waitCorr;
                        RustPrivateNavigation.Frame frame = new RustPrivateNavigation.Frame(); frame.Id = id; frame.Operation = 5;
                        frame.Origin = claim == null ? (byte)0 : (byte)1; frame.Session = claim == null ? 0uL : claim.Connection;
                        frame.Deadline = checked((ulong)SimClock.CurrentTicks + 45uL); frame.Request = request;
                        byte[] record = RustPrivateNavigation.Encode(frame);
                        return "EVENT|PrivateNavigation|" + id.ToString(CultureInfo.InvariantCulture) + "|0|" +
                            record.Length.ToString(CultureInfo.InvariantCulture) + "|" + RustPrivateNavigation.Hex(record);
                    }, Sender, 0.75, RouteRecorder.YieldEnv.Instance, out reason) as RustPrivateNavigation.Envelope;
                if (result == null) return null;
                retained = true; requestId = id; return result;
            }
            finally { if (!retained) Cancel(id); }
        }
        internal static void BindRoute(ulong id, RouteRecorder route)
        {
            Pending p;
            if (!sPlans.TryGetValue(id, out p) || p.Envelope == null) throw new FormatException("route offer retired");
            p.Route = route;
        }
        internal static void Started(ulong id)
        {
            Pending p;
            if (sPlans.TryGetValue(id, out p)) p.Started = true;
        }
        internal static void Tick()
        {
            List<ulong> ended = new List<ulong>();
            foreach (KeyValuePair<ulong, Pending> pair in sPlans)
                if (pair.Value.Started && pair.Value.Route != null && pair.Value.Route.PrivateFinished) ended.Add(pair.Key);
            foreach (ulong id in ended)
            {
                Pending pending;
                if (sPlans.TryGetValue(id, out pending)) pending.Route.RetirePrivatePlan();
            }
        }
        private static void Revoke(ulong id)
        {
            Pending p;
            if (!sPlans.TryGetValue(id, out p)) return;
            sPlans.Remove(id); RustBridgeWait.Offer("private-navigation", p.WaitCorr, null, "cancelled");
            if (p.Route != null) p.Route.RevokePrivatePlan();
        }
        internal static void Cancel(ulong id)
        {
            if (id == 0 || !sPlans.Remove(id)) return;
            RustPrivateNavigation.Frame frame = new RustPrivateNavigation.Frame(); frame.Id = id; frame.Operation = 7;
            if (Sender != null) RustPrivateNavigation.Send(frame, Sender);
        }
        private static int Quantize(float value, int scale)
        {
            double scaled = (double)value * scale;
            if (double.IsNaN(scaled) || double.IsInfinity(scaled)) throw new FormatException("nonfinite measurement");
            return checked((int)(scaled < 0 ? Math.Ceiling(scaled - 0.5) : Math.Floor(scaled + 0.5)));
        }
        internal static int[] Cm(Vector3 p) { return new int[] { Quantize(p.x, 100), Quantize(p.y, 100), Quantize(p.z, 100) }; }
        private static int[] Mm(Vector3 p) { return new int[] { Quantize(p.x, 1000), Quantize(p.y, 1000), Quantize(p.z, 1000) }; }
        private static RustPrivateNavigation.Identity Identity(Vector3 p)
        {
            LotLocation location = LotLocation.Invalid; ulong lot = World.GetLotLocationAtFloor(p, ref location);
            RustPrivateNavigation.Identity identity = new RustPrivateNavigation.Identity();
            identity.Lot = lot; identity.Room = unchecked((ulong)(ushort)World.GetRoomId(lot, location)); identity.Level = location.mLevel;
            return identity;
        }
        private sealed class Face
        {
            internal Vector3 Position;
            internal RustMeasuredSimNav.Surface Surface;
        }
        private static string VertexKey(int[] p)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return p[0].ToString(c) + ":" + p[1].ToString(c) + ":" + p[2].ToString(c);
        }
        private static Vector3 TilePosition(ulong lot, int x, int z, sbyte level)
        {
            Vector3 p = Vector3.Invalid;
            World.GetWorldPosition(lot, new LotLocation(x, z, level, 0), ref p);
            return p;
        }
        private static RustMeasuredSimNav Capture(ulong lotId, ulong actor)
        {
            Lot lot = LotManager.GetLot(lotId); Sim reference = new ObjectGuid(actor).ObjectFromId<Sim>();
            if (lot == null || reference == null) throw new FormatException("lot or reference actor unavailable");
            RustMeasuredSimNav nav = new RustMeasuredSimNav(lotId);
            Vector3 origin = TilePosition(lotId, 0, 0, 0);
            Vector3 dx = TilePosition(lotId, 1, 0, 0) - origin, dz = TilePosition(lotId, 0, 1, 0) - origin;
            dx.y = 0; dz.y = 0;
            if (dx.Length() == 0 || dz.Length() == 0) throw new FormatException("tile transform degenerate");
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = lot.Corners[i] - origin;
                double localX = (corner.x * dx.x + corner.z * dx.z) / (dx.x * dx.x + dx.z * dx.z);
                double localZ = (corner.x * dz.x + corner.z * dz.z) / (dz.x * dz.x + dz.z * dz.z);
                minX = Math.Min(minX, localX); maxX = Math.Max(maxX, localX);
                minZ = Math.Min(minZ, localZ); maxZ = Math.Max(maxZ, localZ);
            }
            int width = checked((int)Math.Round(maxX - minX));
            int height = checked((int)Math.Round(maxZ - minZ));
            LotDisplayLevelInfo levels = World.LotGetDisplayLevelInfo(lotId);
            if (width < 1 || height < 1 || width > 256 || height > 256) throw new FormatException("lot bounds");
            List<Face> faces = new List<Face>();
            Dictionary<ulong, GameObject> measuredObjects = new Dictionary<ulong, GameObject>();
            int measured = 0;
            for (int level = Math.Min(0, levels.mMin); level <= levels.mMax; level++)
            for (int x = -1; x <= width; x++)
            for (int z = -1; z <= height; z++)
            {
                if (++measured % 32 == 0) Simulator.Sleep(0);
                bool exterior = x < 0 || z < 0 || x == width || z == height;
                if (exterior && level != 0) continue;
                LotLocation location = new LotLocation(x, z, checked((sbyte)level), 0);
                Vector3 p = TilePosition(lotId, x, z, (sbyte)level);
                if (!World.HasSolidFloor(lotId, location) && (level != 0 || !World.IsSimRoutable(p.x, p.z))) continue;
                LotLocation actual = LotLocation.Invalid;
                ulong actualLot = World.GetLotLocationAtFloor(p, ref actual);
                if (actual.mLevel != level) continue;
                short room = World.GetRoomId(actualLot, actual);
                if (unchecked((ushort)room) == Lot.kInvalidRoomId || World.HasPool(actualLot, actual)) continue;
                // Native LotLocation is a tile center (ICALL RVA 0x38ea80); basis vectors are measured above.
                Vector3[] corners = new Vector3[] { p - dx * 0.5f - dz * 0.5f, p - dx * 0.5f + dz * 0.5f,
                    p + dx * 0.5f + dz * 0.5f, p + dx * 0.5f - dz * 0.5f };
                int[][] vertices = new int[4][];
                for (int i = 0; i < 4; i++)
                {
                    corners[i].y = level == 0 ? World.GetTerrainHeight(corners[i].x, corners[i].z) :
                        World.GetLevelHeight(corners[i].x, corners[i].z, level);
                    vertices[i] = Cm(corners[i]);
                }
                int count = World.HasDiagonalWall(actualLot, actual) ? 4 : 1;
                for (int part = 0; part < count; part++)
                {
                    int[][] polygon = vertices;
                    Vector3 sample = p;
                    if (count == 4)
                    {
                        sample = (corners[part] + corners[(part + 1) % 4] + p) / 3f;
                        LotLocation sampleLocation = LotLocation.Invalid;
                        actualLot = World.GetLotLocationAtFloor(sample, ref sampleLocation);
                        if (sampleLocation.mLevel != level) continue;
                        room = World.GetRoomId(actualLot, sampleLocation);
                        if (unchecked((ushort)room) == Lot.kInvalidRoomId || World.HasPool(actualLot, sampleLocation)) continue;
                        polygon = new int[][] { vertices[part], vertices[(part + 1) % 4], Cm(p) };
                    }
                    Face face = new Face(); face.Position = sample;
                    face.Surface = nav.AddSurface(actualLot, unchecked((ulong)(ushort)room), (short)level, polygon);
                    faces.Add(face);
                    LotLocation objectLocation = LotLocation.Invalid;
                    ulong objectLot = World.GetLotLocationAtFloor(sample, ref objectLocation);
                    ObjectGuid[] objects = World.GetObjects(objectLot, objectLocation);
                    if (objects != null)
                        foreach (ObjectGuid objectId in objects)
                        {
                            GameObject obj = objectId.ObjectFromId<GameObject>();
                            if (obj != null) measuredObjects[objectId.Value] = obj;
                        }
                }
            }
            bool crossLot = false;
            Dictionary<string, List<Face>> edges = new Dictionary<string, List<Face>>();
            List<string> edgeOrder = new List<string>();
            foreach (Face face in faces)
            {
                int[][] vertices = face.Surface.Vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    string a = VertexKey(vertices[i]), b = VertexKey(vertices[(i + 1) % vertices.Length]);
                    string key = string.CompareOrdinal(a, b) < 0 ? a + "/" + b : b + "/" + a;
                    List<Face> touching;
                    if (!edges.TryGetValue(key, out touching))
                    { touching = new List<Face>(); edges.Add(key, touching); edgeOrder.Add(key); }
                    touching.Add(face);
                }
            }
            foreach (string key in edgeOrder)
            {
                List<Face> touching = edges[key];
                if (touching.Count > 2) throw new FormatException("overlapping measured surfaces");
                if (touching.Count != 2) continue;
                Face a = touching[0], b = touching[1];
                if (a.Surface.Room != b.Surface.Room || a.Surface.Level != b.Surface.Level ||
                    World.WallExistsBetweenPoints(lotId, a.Position, b.Position, (eRoomDefinition)0)) continue;
                List<int[]> shared = new List<int[]>();
                foreach (int[] va in a.Surface.Vertices)
                foreach (int[] vb in b.Surface.Vertices) if (Same(va, vb)) shared.Add(va);
                if (shared.Count != 2 || shared[0][1] != shared[1][1]) continue;
                nav.AddPortal(a.Surface, b.Surface, 0, 0, 0, 0, new int[] { shared[0][0], shared[0][2] },
                    new int[] { shared[1][0], shared[1][2] }, shared[0][1], false, false);
                crossLot |= a.Surface.Lot != b.Surface.Lot;
            }
            foreach (GameObject obj in lot.GetObjects<GameObject>()) measuredObjects[obj.ObjectId.Value] = obj;
            List<GameObject> orderedObjects = new List<GameObject>(measuredObjects.Values);
            orderedObjects.Sort(delegate(GameObject a, GameObject b) { return a.ObjectId.Value.CompareTo(b.ObjectId.Value); });
            nav.Order();
            foreach (GameObject obj in orderedObjects)
            {
                Door door = obj as Door; Stairs stairs = obj as Stairs;
                if (door == null && stairs == null) continue;
                PortalComponent portal = obj.PortalComponent;
                if (portal == null || portal.GetNumLanes() == 0) throw new FormatException("portal lane unavailable");
                Slot[] slots = portal.GetSlotsForLaneIndex(0u);
                if (slots == null || slots.Length != 2) throw new FormatException("portal lane shape");
                Vector3 zero = Slots.GetPositionOfSlot(obj.ObjectId, (uint)slots[0]);
                Vector3 one = Slots.GetPositionOfSlot(obj.ObjectId, (uint)slots[1]);
                RustMeasuredSimNav.Surface a = FindSurface(nav, zero), b = FindSurface(nav, one);
                if (a == null || b == null || ReferenceEquals(a, b)) throw new FormatException("portal supporting cell unavailable");
                if (a.Id > b.Id) { RustMeasuredSimNav.Surface swap = a; a = b; b = swap; }
                bool slotZeroIsEntry = RustMeasuredSimNav.SlotZeroIsEntry(Cm(zero), Cm(one), a);
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
                bool haveFootprint = false;
                uint[] portalFootprints = World.ObjectGetFootprintNameHashes(obj.ObjectId);
                if (portalFootprints != null)
                    foreach (uint footprint in portalFootprints)
                    {
                        if (!World.ObjectIsFootprintEnabled(obj.ObjectId, footprint)) continue;
                        Vector2 measuredMin, measuredMax; sbyte footprintLevel;
                        if (!World.ObjectGetFootprintExtents(obj.ObjectId, footprint, out measuredMin, out measuredMax, out footprintLevel))
                            throw new FormatException("portal footprint unavailable");
                        min.x = Math.Min(min.x, measuredMin.x); min.y = Math.Min(min.y, measuredMin.y);
                        max.x = Math.Max(max.x, measuredMax.x); max.y = Math.Max(max.y, measuredMax.y); haveFootprint = true;
                    }
                if (!haveFootprint) throw new FormatException("portal footprint unavailable");
                Vector3 center = (zero + one) * 0.5f; Vector3 direction = one - zero; direction.y = 0;
                if (direction.Length() == 0) throw new FormatException("portal direction degenerate");
                direction.Normalize();
                Vector3 lateral = new Vector3(-direction.z, 0, direction.x);
                float half = float.MaxValue;
                if (Math.Abs(lateral.x) > 0) half = Math.Min(half, Math.Min(Math.Abs(max.x - center.x), Math.Abs(center.x - min.x)) / Math.Abs(lateral.x));
                if (Math.Abs(lateral.z) > 0) half = Math.Min(half, Math.Min(Math.Abs(max.y - center.z), Math.Abs(center.z - min.y)) / Math.Abs(lateral.z));
                Vector3 j0 = center - lateral * half, j1 = center + lateral * half;
                bool locked = door != null && (door.IsLocked || door.OneWayRoomDestinationId != Lot.kInvalidRoomId ||
                    lot.IsRoomHidden((int)a.Room) || lot.IsRoomHidden((int)b.Room));
                bool allowed = door != null && door.IsAllowedThrough((int)a.Room, (int)b.Room, reference) == PortalRestriction.None &&
                    door.IsAllowedThrough((int)b.Room, (int)a.Room, reference) == PortalRestriction.None;
                nav.AddPortal(a, b, stairs == null ? 0x80000000u : 0x80000001u, obj.ObjectId.Value,
                    (uint)slots[slotZeroIsEntry ? 0 : 1], (uint)slots[slotZeroIsEntry ? 1 : 0],
                    new int[] { Cm(j0)[0], Cm(j0)[2] }, new int[] { Cm(j1)[0], Cm(j1)[2] },
                    Cm(center)[1], locked, allowed);
            }
            foreach (GameObject obj in orderedObjects)
            {
                if (obj.ObjectId.Value == actor || obj is Door || obj is Stairs) continue;
                uint[] footprints = World.ObjectGetFootprintNameHashes(obj.ObjectId);
                if (footprints == null) continue;
                foreach (uint footprint in footprints)
                {
                    if (!World.ObjectIsFootprintEnabled(obj.ObjectId, footprint)) continue;
                    Vector2 min, max; sbyte floor;
                    if (!World.ObjectGetFootprintExtents(obj.ObjectId, footprint, out min, out max, out floor))
                        throw new FormatException("obstacle footprint unavailable");
                    Vector3 at = obj.Position;
                    at.y = World.GetLevelHeight(at.x, at.z, floor);
                    int[] originCm = Cm(at);
                    nav.AddObstacle(obj.ObjectId.Value, originCm,
                        new int[] { Quantize(min.x, 100) - originCm[0], 0, Quantize(min.y, 100) - originCm[2] },
                        new int[] { Quantize(max.x, 100) - originCm[0], 0, Quantize(max.y, 100) - originCm[2] });
                }
            }
            if (!crossLot) throw new FormatException("measured cross-lot connection unavailable");
            return nav;
        }
        private static RustMeasuredSimNav.Surface FindSurface(RustMeasuredSimNav nav, Vector3 point)
        {
            RustPrivateNavigation.Identity identity = Identity(point); int[] cm = Cm(point);
            RustMeasuredSimNav.Surface nearest = null; double best = double.MaxValue;
            foreach (RustMeasuredSimNav.Surface s in nav.Cells)
            {
                if (s.Lot != identity.Lot || s.Room != identity.Room || s.Level != identity.Level) continue;
                if (RustMeasuredSimNav.Contains(s, cm)) return s;
                double x = (double)s.Center[0] - cm[0], y = (double)s.Center[1] - cm[1], z = (double)s.Center[2] - cm[2];
                double distance = x * x + y * y + z * z;
                if (distance < best) { best = distance; nearest = s; }
            }
            return nearest;
        }
    }
#endif
}
