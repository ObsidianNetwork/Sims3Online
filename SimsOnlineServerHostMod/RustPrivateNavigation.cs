// PURE C# 2. Packed private v1 bytes; no engine references, geometry logging or disk IO.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    public static class RustPrivateNavigation
    {
        public const int MaxRecord = 128 * 1024 * 1024;
        public const int MaxEnvelope = 1024 * 1024;
        public const int MaxSimNav = 64 * 1024 * 1024;

        public sealed class Cell
        {
            public uint Id;
            public ulong Lot, Room;
            public short Level;
        }
        public sealed class Portal
        {
            public uint Id, EntrySlot, ExitSlot;
            public ulong Object;
            public bool CrossLot;
        }
        public sealed class Obstacle
        {
            public ulong Object;
            public uint Template, Scale;
            public int[] Origin = new int[3];
        }
        public sealed class Actor
        {
            public ulong Wire;
            public uint Kernel;
        }
        public sealed class Bindings
        {
            public uint Schema = 1;
            public ulong Epoch, Generation;
            public byte Provenance;
            public Cell[] Cells = new Cell[0];
            public Portal[] Portals = new Portal[0];
            public ushort[] Keys = new ushort[0];
            public Obstacle[] Obstacles = new Obstacle[0];
            public Actor[] Actors = new Actor[0];
        }
        public sealed class Identity
        {
            public ulong Lot, Room;
            public short Level;
        }
        public sealed class Request
        {
            public ulong Epoch, Generation, Correlation, Actor;
            public uint Kernel, Flags;
            public int[] Start = new int[3], Goal = new int[3];
            public Identity StartIdentity = new Identity(), GoalIdentity = new Identity();
        }
        public sealed class Frame
        {
            public byte Operation, OriginalOperation, Origin;
            public ushort Status, Port, ClientPort, ServerPort;
            public uint Pid;
            public ulong Id, Epoch, Generation, Session, Deadline;
            public byte[] ClientIp = new byte[4], ServerIp = new byte[4], Data = new byte[0];
            public Bindings Bindings;
            public Request Request;
        }
        public sealed class Leg
        {
            public uint Index, Cell, PathType, EntrySlot, ExitSlot;
            public byte Kind;
            public int[] From = new int[3], To = new int[3];
            public ulong Object;
        }
        public sealed class Envelope
        {
            public ulong Epoch, Generation, Correlation, Actor, Hash, Cost;
            public int[] Start = new int[3], Goal = new int[3];
            public Leg[] Legs = new Leg[0];
        }

        private sealed class Reader
        {
            private readonly byte[] mBytes;
            private int mOffset;
            internal Reader(byte[] bytes, int cap)
            {
                if (bytes == null || bytes.Length > cap) throw new FormatException("size");
                mBytes = bytes;
            }
            internal int Left { get { return mBytes.Length - mOffset; } }
            internal ulong Number(int width)
            {
                if (Left < width) throw new FormatException("truncated");
                ulong value = 0;
                for (int i = 0; i < width; i++) value |= (ulong)mBytes[mOffset++] << (8 * i);
                return value;
            }
            internal byte U8() { return (byte)Number(1); }
            internal ushort U16() { return (ushort)Number(2); }
            internal uint U32() { return (uint)Number(4); }
            internal ulong U64() { return Number(8); }
            internal int I32() { return unchecked((int)U32()); }
            internal int Count(int width, int cap)
            {
                uint n = U32();
                if (n > (uint)cap || n > (uint)(Left / width)) throw new FormatException("count");
                return (int)n;
            }
            internal byte[] Bytes(int count)
            {
                if (count < 0 || count > Left) throw new FormatException("truncated");
                byte[] copy = new byte[count];
                Array.Copy(mBytes, mOffset, copy, 0, count);
                mOffset += count;
                return copy;
            }
            internal int[] Xyz() { return new int[] { I32(), I32(), I32() }; }
            internal Identity Identity()
            {
                Identity identity = new Identity();
                identity.Lot = U64(); identity.Room = U64(); identity.Level = unchecked((short)U16());
                return identity;
            }
            internal void End() { if (Left != 0) throw new FormatException("trailing"); }
        }
        internal sealed class Writer
        {
            private readonly List<byte> mBytes = new List<byte>();
            private readonly int mCap;
            internal Writer(int cap) { mCap = cap; }
            internal void Number(ulong value, int width)
            {
                if (mBytes.Count > mCap - width) throw new FormatException("size");
                for (int i = 0; i < width; i++) mBytes.Add((byte)(value >> (8 * i)));
            }
            internal void U8(byte n) { Number(n, 1); }
            internal void U16(ushort n) { Number(n, 2); }
            internal void U32(uint n) { Number(n, 4); }
            internal void U64(ulong n) { Number(n, 8); }
            internal void I32(int n) { U32(unchecked((uint)n)); }
            internal void Bytes(byte[] bytes)
            {
                if (bytes == null || bytes.Length > mCap - mBytes.Count) throw new FormatException("size");
                mBytes.AddRange(bytes);
            }
            internal void Xyz(int[] xyz)
            {
                if (xyz == null || xyz.Length != 3) throw new FormatException("xyz");
                for (int i = 0; i < 3; i++) I32(xyz[i]);
            }
            internal void Identity(Identity identity)
            {
                U64(identity.Lot); U64(identity.Room); U16(unchecked((ushort)identity.Level));
            }
            internal byte[] Done() { return mBytes.ToArray(); }
        }

        public static Frame Decode(byte[] bytes)
        {
            Reader r = new Reader(bytes, MaxRecord);
            if (r.U32() != (uint)r.Left) throw new FormatException("length");
            if (r.U16() != 1) throw new FormatException("version");
            Frame f = new Frame();
            f.Operation = r.U8();
            if (r.U8() != 0) throw new FormatException("reserved");
            f.Id = r.U64();
            switch (f.Operation)
            {
                case 1: f.Pid = r.U32(); f.Port = r.U16(); break;
                case 2:
                    f.Session = r.U64(); f.ClientIp = r.Bytes(4); f.ClientPort = r.U16();
                    f.ServerIp = r.Bytes(4); f.ServerPort = r.U16(); break;
                case 3:
                    f.Bindings = ReadBindings(r);
                    f.Data = r.Bytes(r.Count(1, MaxSimNav)); break;
                case 4: f.Epoch = r.U64(); f.Generation = r.U64(); break;
                case 5:
                    f.Origin = r.U8(); f.Session = r.U64();
                    if (!((f.Origin == 0 && f.Session == 0) || (f.Origin == 1 && f.Session != 0)))
                        throw new FormatException("origin");
                    f.Deadline = r.U64();
                    Request request = new Request();
                    request.Epoch = r.U64(); request.Generation = r.U64(); request.Correlation = r.U64();
                    request.Actor = r.U64(); request.Kernel = r.U32();
                    request.Start = r.Xyz(); request.Goal = r.Xyz();
                    request.StartIdentity = r.Identity(); request.GoalIdentity = r.Identity(); request.Flags = r.U32();
                    f.Request = request; break;
                case 6: case 7: break;
                case 8:
                    f.OriginalOperation = r.U8(); f.Status = r.U16();
                    if (f.OriginalOperation < 2 || f.OriginalOperation > 7 || f.Status > 10)
                        throw new FormatException("result");
                    f.Epoch = r.U64(); f.Generation = r.U64();
                    f.Data = r.Bytes(r.Count(1, MaxEnvelope)); break;
                default: throw new FormatException("operation");
            }
            r.End();
            return f;
        }

        private static Bindings ReadBindings(Reader r)
        {
            Bindings b = new Bindings();
            b.Schema = r.U32(); b.Epoch = r.U64(); b.Generation = r.U64(); b.Provenance = r.U8();
            if (b.Provenance > 2) throw new FormatException("provenance");
            b.Cells = new Cell[r.Count(22, MaxRecord / 22)];
            for (int i = 0; i < b.Cells.Length; i++)
            {
                Cell c = new Cell();
                c.Id = r.U32(); c.Lot = r.U64(); c.Room = r.U64(); c.Level = unchecked((short)r.U16());
                b.Cells[i] = c;
            }
            b.Portals = new Portal[r.Count(21, MaxRecord / 21)];
            for (int i = 0; i < b.Portals.Length; i++)
            {
                Portal p = new Portal();
                p.Id = r.U32(); p.Object = r.U64(); p.EntrySlot = r.U32(); p.ExitSlot = r.U32();
                byte cross = r.U8();
                if (cross > 1) throw new FormatException("boolean");
                p.CrossLot = cross == 1; b.Portals[i] = p;
            }
            b.Keys = new ushort[r.Count(2, MaxRecord / 2)];
            for (int i = 0; i < b.Keys.Length; i++) b.Keys[i] = r.U16();
            b.Obstacles = new Obstacle[r.Count(28, MaxRecord / 28)];
            for (int i = 0; i < b.Obstacles.Length; i++)
            {
                Obstacle o = new Obstacle();
                o.Object = r.U64(); o.Template = r.U32(); o.Origin = r.Xyz(); o.Scale = r.U32();
                b.Obstacles[i] = o;
            }
            b.Actors = new Actor[r.Count(12, MaxRecord / 12)];
            for (int i = 0; i < b.Actors.Length; i++)
            {
                Actor a = new Actor(); a.Wire = r.U64(); a.Kernel = r.U32(); b.Actors[i] = a;
            }
            return b;
        }

        public static byte[] Encode(Frame f)
        {
            Writer w = new Writer(MaxRecord);
            w.U32(0); w.U16(1); w.U8(f.Operation); w.U8(0); w.U64(f.Id);
            switch (f.Operation)
            {
                case 1: w.U32(f.Pid); w.U16(f.Port); break;
                case 2:
                    if (f.ClientIp.Length != 4 || f.ServerIp.Length != 4) throw new FormatException("ip");
                    w.U64(f.Session); w.Bytes(f.ClientIp); w.U16(f.ClientPort);
                    w.Bytes(f.ServerIp); w.U16(f.ServerPort); break;
                case 3:
                    WriteBindings(w, f.Bindings);
                    if (f.Data.Length > MaxSimNav) throw new FormatException("size");
                    w.U32((uint)f.Data.Length); w.Bytes(f.Data); break;
                case 4: w.U64(f.Epoch); w.U64(f.Generation); break;
                case 5:
                    w.U8(f.Origin); w.U64(f.Session); w.U64(f.Deadline);
                    Request r = f.Request;
                    w.U64(r.Epoch); w.U64(r.Generation); w.U64(r.Correlation); w.U64(r.Actor); w.U32(r.Kernel);
                    w.Xyz(r.Start); w.Xyz(r.Goal); w.Identity(r.StartIdentity); w.Identity(r.GoalIdentity); w.U32(r.Flags);
                    break;
                case 6: case 7: break;
                case 8:
                    w.U8(f.OriginalOperation); w.U16(f.Status); w.U64(f.Epoch); w.U64(f.Generation);
                    if (f.Data.Length > MaxEnvelope) throw new FormatException("size");
                    w.U32((uint)f.Data.Length); w.Bytes(f.Data); break;
                default: throw new FormatException("operation");
            }
            byte[] bytes = w.Done();
            uint length = (uint)(bytes.Length - 4);
            for (int i = 0; i < 4; i++) bytes[i] = (byte)(length >> (8 * i));
            Decode(bytes);
            return bytes;
        }

        private static void WriteBindings(Writer w, Bindings b)
        {
            w.U32(b.Schema); w.U64(b.Epoch); w.U64(b.Generation); w.U8(b.Provenance);
            w.U32((uint)b.Cells.Length);
            foreach (Cell c in b.Cells)
            { w.U32(c.Id); w.U64(c.Lot); w.U64(c.Room); w.U16(unchecked((ushort)c.Level)); }
            w.U32((uint)b.Portals.Length);
            foreach (Portal p in b.Portals)
            { w.U32(p.Id); w.U64(p.Object); w.U32(p.EntrySlot); w.U32(p.ExitSlot); w.U8(p.CrossLot ? (byte)1 : (byte)0); }
            w.U32((uint)b.Keys.Length);
            foreach (ushort key in b.Keys) w.U16(key);
            w.U32((uint)b.Obstacles.Length);
            foreach (Obstacle o in b.Obstacles)
            { w.U64(o.Object); w.U32(o.Template); w.Xyz(o.Origin); w.U32(o.Scale); }
            w.U32((uint)b.Actors.Length);
            foreach (Actor a in b.Actors) { w.U64(a.Wire); w.U32(a.Kernel); }
        }

        public static Envelope DecodeEnvelope(byte[] bytes)
        {
            Reader r = new Reader(bytes, MaxEnvelope);
            if (r.U32() != 1) throw new FormatException("version");
            Envelope e = new Envelope();
            e.Epoch = r.U64(); e.Generation = r.U64(); e.Correlation = r.U64(); e.Actor = r.U64();
            e.Start = r.Xyz(); e.Goal = r.Xyz();
            uint count = r.U32();
            e.Hash = r.U64(); e.Cost = r.U64();
            if (count == 0 || count > (uint)(r.Left / 53)) throw new FormatException("count");
            if (e.Epoch == 0 || e.Generation == 0 || e.Correlation == 0 || e.Actor == 0)
                throw new FormatException("identity");
            e.Legs = new Leg[(int)count];
            for (int i = 0; i < e.Legs.Length; i++)
            {
                Leg leg = new Leg();
                leg.Index = r.U32(); leg.Cell = r.U32(); leg.Kind = r.U8(); leg.PathType = r.U32();
                leg.From = r.Xyz(); leg.To = r.Xyz(); leg.Object = r.U64(); leg.EntrySlot = r.U32(); leg.ExitSlot = r.U32();
                if (leg.Index != (uint)i || leg.Kind > 4) throw new FormatException("leg");
                e.Legs[i] = leg;
            }
            r.End();
            return e;
        }

        public static byte[] EncodeEnvelope(Envelope e)
        {
            if (e.Legs.Length > (MaxEnvelope - 80) / 53) throw new FormatException("size");
            Writer w = new Writer(MaxEnvelope);
            w.U32(1); w.U64(e.Epoch); w.U64(e.Generation); w.U64(e.Correlation); w.U64(e.Actor);
            w.Xyz(e.Start); w.Xyz(e.Goal); w.U32((uint)e.Legs.Length); w.U64(e.Hash); w.U64(e.Cost);
            foreach (Leg leg in e.Legs)
            {
                w.U32(leg.Index); w.U32(leg.Cell); w.U8(leg.Kind); w.U32(leg.PathType);
                w.Xyz(leg.From); w.Xyz(leg.To); w.U64(leg.Object); w.U32(leg.EntrySlot); w.U32(leg.ExitSlot);
            }
            byte[] bytes = w.Done();
            DecodeEnvelope(bytes);
            return bytes;
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }
        public static byte[] Unhex(string hex)
        {
            if (hex == null || (hex.Length & 1) != 0 || hex.Length > MaxRecord * 2) throw new FormatException("hex");
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out bytes[i])) throw new FormatException("hex");
            return bytes;
        }
        public static string BlobName(ulong epoch, ulong generation, ulong corr, ulong actor, ulong id)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "nav-" + epoch.ToString("x16", c) + "-" + generation.ToString("x16", c) + "-" +
                corr.ToString("x16", c) + "-" + actor.ToString("x16", c) + "-" + id.ToString("x16", c);
        }
        public static string OfferAcknowledgement(string blob, ulong id, ulong epoch, ulong generation, ulong corr, ulong actor)
        {
            if (blob != BlobName(epoch, generation, corr, actor, id)) throw new FormatException("blob");
            CultureInfo c = CultureInfo.InvariantCulture;
            return "rust-navigation-offer=accepted;blob=" + blob + ";request=" + id.ToString(c) +
                ";epoch=" + epoch.ToString(c) + ";generation=" + generation.ToString(c) +
                ";corr=" + corr.ToString(c) + ";actor=" + actor.ToString(c);
        }
        public static string ResultAcknowledgement(byte[] bytes)
        {
            Frame f = Decode(bytes);
            if (f.Operation != 7 && f.Operation != 8) throw new FormatException("operation");
            CultureInfo c = CultureInfo.InvariantCulture;
            return "rust-navigation-result=accepted;operation=" + f.Operation.ToString(c) +
                ";request=" + f.Id.ToString(c) + ";frame=" + Hex(bytes);
        }

        // Active claims replace atomically; assigned IDs survive releases within one controller session.
        public sealed class SyncStore
        {
            public delegate void PlanPurge();
            private readonly PlanPurge mDiscardPlans;
            public SyncStore() { }
            public SyncStore(PlanPurge discardPlans) { mDiscardPlans = discardPlans; }
            public sealed class Claim
            {
                public ulong Connection, Wire;
                public int Player;
                public uint Kernel;
            }
            private readonly SortedDictionary<uint, Claim> mActive = new SortedDictionary<uint, Claim>();
            private readonly SortedDictionary<uint, Claim> mKnown = new SortedDictionary<uint, Claim>();
            private readonly List<Claim> mStaged = new List<Claim>();
            private ulong mStageEpoch, mFencedEpoch, mNextFrame = 1, mPlanCorr, mFenceId;
            private uint mStageCount, mStageNext;
            public ulong Epoch, Generation = 1;
            public bool Synced, Fenced;

            public void ResetAttachment()
            {
                mActive.Clear(); mKnown.Clear(); AbortStage();
                mNextFrame = 1; mFenceId = 0; Generation = 1; Synced = false; Fenced = false;
                // Keep the prior fence: CONNECT is not permission to reuse corr within that epoch.
            }
            private void AbortStage() { mStaged.Clear(); mStageEpoch = 0; mStageCount = 0; mStageNext = 0; }
            public void AbortSync() { AbortStage(); }
            public string AcceptPage(ulong epoch, uint index, uint count, string entries)
            {
                try
                {
                    if (epoch == 0 || epoch < Epoch || (!Synced && epoch <= mFencedEpoch) ||
                        count == 0 || count > 256 || index >= count || entries == null || entries.Length > 3500)
                        throw new FormatException("sync header");
                    for (int i = 0; i < entries.Length; i++)
                        if (entries[i] > 127) throw new FormatException("sync ASCII");
                    if (index == 0)
                    {
                        AbortStage(); mStageEpoch = epoch; mStageCount = count;
                    }
                    if (mStageEpoch != epoch || mStageCount != count || index != mStageNext)
                        throw new FormatException("sync page order");
                    int pageEntries = 0;
                    if (entries == "none")
                    {
                        if (index != 0 || count != 1) throw new FormatException("sync empty");
                    }
                    else
                    {
                        string[] rows = entries.Split(',');
                        foreach (string row in rows)
                        {
                            string[] f = row.Split(':');
                            Claim c = new Claim();
                            if (f.Length != 4 ||
                                !ulong.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out c.Connection) || c.Connection == 0 ||
                                !int.TryParse(f[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out c.Player) ||
                                !ulong.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out c.Wire) || c.Wire == 0 ||
                                !uint.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out c.Kernel) || c.Kernel < 2 ||
                                (mStaged.Count > 0 && c.Kernel <= mStaged[mStaged.Count - 1].Kernel))
                                throw new FormatException("sync entry");
                            foreach (Claim known in mKnown.Values)
                                if ((known.Wire == c.Wire && known.Kernel != c.Kernel) ||
                                    (known.Kernel == c.Kernel && known.Wire != c.Wire)) throw new FormatException("sync IdMap");
                            foreach (Claim staged in mStaged)
                                if (staged.Wire == c.Wire || staged.Kernel == c.Kernel) throw new FormatException("sync duplicate");
                            if (mStaged.Count == 256) throw new FormatException("sync capacity");
                            mStaged.Add(c); pageEntries++;
                        }
                    }
                    // This tick's page-zero ack is the producer barrier, even for a multi-page replacement.
                    if (index == 0 && epoch > Epoch && mDiscardPlans != null) mDiscardPlans();
                    mStageNext++;
                    if (mStageNext == count)
                    {
                        bool bump = epoch != Epoch;
                        mActive.Clear();
                        foreach (Claim c in mStaged) { mActive.Add(c.Kernel, c); mKnown[c.Kernel] = c; }
                        Epoch = epoch; Synced = true;
                        if (bump) { Fenced = false; Generation = 1; mNextFrame = 1; mFenceId = 0; }
                        AbortStage();
                    }
                    CultureInfo ci = CultureInfo.InvariantCulture;
                    return "rust-navigation-sync=accepted;epoch=" + epoch.ToString(ci) + ";index=" + index.ToString(ci) +
                        ";count=" + count.ToString(ci) + ";entries=" + pageEntries.ToString(ci) + ";active=" + mActive.Count.ToString(ci);
                }
                catch (FormatException) { AbortStage(); throw; }
            }
            public Claim SelectActor(ulong actor)
            {
                foreach (Claim c in mActive.Values) if (actor == 0 || c.Wire == actor) return c;
                return null;
            }
            public Actor[] KnownActors()
            {
                List<Actor> actors = new List<Actor>();
                foreach (Claim c in mKnown.Values) { Actor a = new Actor(); a.Wire = c.Wire; a.Kernel = c.Kernel; actors.Add(a); }
                return actors.ToArray();
            }
            public uint KernelFor(ulong actor)
            {
                foreach (Claim c in mKnown.Values) if (c.Wire == actor) return c.Kernel;
                return 0;
            }
            public ulong NextFrameId()
            {
                if (mNextFrame == ulong.MaxValue) throw new FormatException("frame exhausted");
                return mNextFrame++;
            }
            public ulong NextPlanCorr()
            {
                if (!Fenced || mPlanCorr == ulong.MaxValue) throw new FormatException("plan fence");
                return ++mPlanCorr;
            }
            public Frame BeginInvalidate()
            {
                if (!Synced || Epoch == 0) throw new FormatException("sync required");
                Frame frame = new Frame(); frame.Id = NextFrameId(); frame.Operation = 4;
                frame.Epoch = Epoch; frame.Generation = Generation;
                mFenceId = frame.Id; Fenced = false;
                return frame;
            }
            public Frame ApplyInvalidateResult(Frame result)
            {
                if (result.Operation != 8 || result.OriginalOperation != 4 || mFenceId == 0 || result.Id != mFenceId)
                    throw new FormatException("invalidate correlation");
                if (result.Status == 3)
                {
                    Epoch = Math.Max(Epoch, result.Epoch); Generation = result.Generation;
                    return BeginInvalidate();
                }
                if (result.Status != 1 || result.Epoch != Epoch || result.Generation != Generation || Epoch < mFencedEpoch)
                    throw new FormatException("invalidate refused");
                if (Epoch > mFencedEpoch) mPlanCorr = 0;
                mFencedEpoch = Epoch; mFenceId = 0; Fenced = true;
                return null;
            }
            public ulong NextInstallGeneration()
            {
                if (!Fenced || Generation == ulong.MaxValue) throw new FormatException("install fence");
                return ++Generation;
            }
        }

        public static void Send(Frame frame, RustRoutePlan.LineSender sender)
        {
            byte[] bytes = Encode(frame);
            CultureInfo c = CultureInfo.InvariantCulture;
            // Serial calls on the simulator task keep one record's fragments contiguous.
            for (int offset = 0; offset < bytes.Length; offset += 1900)
            {
                int length = Math.Min(1900, bytes.Length - offset);
                byte[] chunk = new byte[length];
                Array.Copy(bytes, offset, chunk, 0, length);
                sender("EVENT|PrivateNavigation|" + frame.Id.ToString(c) + "|" + offset.ToString(c) +
                    "|" + bytes.Length.ToString(c) + "|" + Hex(chunk));
            }
        }

        public interface IRouteBuilder
        {
            bool SubPath(Leg leg);
            bool Portal(Leg approach, Leg through, Leg exit);
        }
        public static bool BuildTypedRoute(Envelope envelope, IRouteBuilder builder)
        {
            Leg[] legs = envelope.Legs;
            if (legs.Length == 0 || legs[legs.Length - 1].Kind != 4) throw new FormatException("missing handoff");
            for (int i = 0; i < legs.Length; i++)
            {
                Leg leg = legs[i];
                if (leg.Index != (uint)i) throw new FormatException("leg order");
                if (leg.Kind == 0 || leg.Kind == 4)
                {
                    if (leg.PathType != 0 || leg.Object != 0 || (leg.Kind == 4 && i != legs.Length - 1))
                        throw new FormatException("subpath shape");
                    continue;
                }
                if (leg.Kind != 1 || i + 2 >= legs.Length - 1) throw new FormatException("portal approach");
                Leg through = legs[i + 1], exit = legs[i + 2];
                if (through.Index != (uint)i + 1 || exit.Index != (uint)i + 2 || through.Kind != 2 || exit.Kind != 3 ||
                    (through.PathType != 0x80000000u && through.PathType != 0x80000001u) ||
                    leg.PathType != 0 || exit.PathType != 0 || through.Object == 0 || leg.Object != through.Object || exit.Object != through.Object ||
                    leg.EntrySlot != through.EntrySlot || exit.EntrySlot != through.EntrySlot ||
                    leg.ExitSlot != through.ExitSlot || exit.ExitSlot != through.ExitSlot)
                    throw new FormatException("portal binding");
                i += 2;
            }
            for (int i = 0; i < legs.Length - 1; i++)
            {
                if (legs[i].Kind == 0) { if (!builder.SubPath(legs[i])) return false; }
                else { if (!builder.Portal(legs[i], legs[i + 1], legs[i + 2])) return false; i += 2; }
            }
            return true;
        }

        // Deliberately separate from DIAG_RUN's single upload slot. Completed bytes live only in memory.
        public sealed class BlobStore
        {
            private sealed class Upload
            {
                internal int Count, Received;
                internal readonly StringBuilder Hex = new StringBuilder();
                internal byte[] Bytes;
            }
            private readonly Dictionary<string, Upload> mUploads = new Dictionary<string, Upload>();
            public bool Add(string id, int index, int count, string hex, out byte[] complete)
            {
                complete = null;
                if (id == null || !id.StartsWith("nav-", StringComparison.Ordinal) || id.Length > 128 ||
                    count <= 0 || count > MaxEnvelope || index < 0 || index >= count ||
                    hex == null || hex.Length == 0 || hex.Length > 3800 || (hex.Length & 1) != 0) return false;
                for (int i = 0; i < hex.Length; i++)
                    if (!((hex[i] >= '0' && hex[i] <= '9') || (hex[i] >= 'A' && hex[i] <= 'F') ||
                        (hex[i] >= 'a' && hex[i] <= 'f'))) return false;
                Upload u;
                bool found = mUploads.TryGetValue(id, out u);
                if (!found)
                {
                    if (index != 0 || mUploads.Count >= 16) return false;
                    u = new Upload(); u.Count = count;
                }
                if (count != u.Count || index != u.Received || u.Bytes != null ||
                    hex.Length > MaxEnvelope * 2 - u.Hex.Length) return false;
                if (!found) mUploads.Add(id, u);
                u.Hex.Append(hex); u.Received++;
                if (u.Received == u.Count)
                {
                    u.Bytes = Unhex(u.Hex.ToString());
                    u.Hex.Length = 0;
                    complete = u.Bytes;
                }
                return true;
            }
            public byte[] Take(string id)
            {
                Upload u;
                if (!mUploads.TryGetValue(id, out u) || u.Bytes == null) return null;
                mUploads.Remove(id);
                return u.Bytes;
            }
            public void Clear() { mUploads.Clear(); }
        }
    }
}
