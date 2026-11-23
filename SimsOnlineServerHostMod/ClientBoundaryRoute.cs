// PURE C# 2. Receipt arrival and observed execution are separate operations.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimsOnlineServerHost
{
    public sealed class ClientBoundaryRoute
    {
        public static readonly ReceiptStore Receipts = new ReceiptStore();
        public const int MaxLegs = (RustPrivateNavigation.MaxEnvelope - 80) / 53;
        public ulong NavEpoch, Correlation, Actor, Hash, Cost;
        public uint LegsTotal;
        public string Phase = "Receiving";
        public long InvalidProgressions;
        private readonly List<string> mFrames = new List<string>();
        private readonly List<uint> mExecuted = new List<uint>();
        private readonly List<uint> mPathTypes = new List<uint>();
        private readonly List<int[]> mTargets = new List<int[]>();

        private static bool U64(string s, out ulong n)
        {
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }
        private static bool U32(string s, out uint n)
        {
            return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
        }
        public static bool TryNavEpoch(string text, out ulong epoch)
        {
            epoch = 0;
            return text != null && text.Length > 0 && text[0] != '0' && U64(text, out epoch) && epoch != 0;
        }
        public static bool IsPlanRecord(string line, bool prefixed)
        {
            if (line == null) return false;
            string[] f = line.Split('|');
            int kind = prefixed ? 1 : 0;
            return (f.Length == kind + 6 && f[kind] == "Rt") ||
                (f.Length == kind + 9 && f[kind] == "Vp" && f[kind + 1] == "rt");
        }
        // Fixed ring slots and snapshot cursor slots keep their indices when purged.
        public static void DiscardPlanRecords(IList<string> lines, bool prefixed)
        {
            for (int i = 0; i < lines.Count; i++)
                if (IsPlanRecord(lines[i], prefixed)) lines[i] = null;
        }
        public static bool IsNativeRoute(string[] f)
        {
            if (f == null || f.Length < 9 || (f[1] != "Rt" && f[1] != "Rs")) return false;
            ulong sim, obj;
            uint slot;
            if (!U64(f[2], out sim) || !U32(f[7], out slot) || !U64(f[8], out obj)) return false;
            for (int i = 3; i <= 5; i++)
            {
                float n;
                if (!float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n) ||
                    float.IsNaN(n) || float.IsInfinity(n)) return false;
            }
            return true;
        }
        public bool OnPlanHeader(string[] f)
        {
            ulong corr, actor, hash, cost;
            uint count;
            if (f == null || f.Length != 7 || f[1] != "Rt" || !U64(f[2], out corr) || corr == 0 ||
                !U64(f[3], out actor) || actor == 0 || !U32(f[4], out count) || count == 0 || count > MaxLegs ||
                f[5].Length != 16 || !ulong.TryParse(f[5], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash) ||
                !U64(f[6], out cost) || mFrames.Count != 0) return false;
            // NumberStyles.AllowHexSpecifier permits no signs or whitespace.
            Correlation = corr; Actor = actor; Hash = hash; Cost = cost; LegsTotal = count;
            mFrames.Add(string.Join("|", f, 1, f.Length - 1));
            return true;
        }
        public bool OnPoint(string[] f)
        {
            ulong corr, actor;
            uint index, path;
            int x, y, z;
            if (f == null || f.Length != 10 || f[1] != "Vp" || f[2] != "rt" || mFrames.Count == 0 ||
                !U64(f[3], out corr) || corr != Correlation || !U64(f[4], out actor) || actor != Actor ||
                !U32(f[5], out index) || index != (uint)mTargets.Count || index >= LegsTotal ||
                !U32(f[6], out path) ||
                !int.TryParse(f[7], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out x) ||
                !int.TryParse(f[8], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out y) ||
                !int.TryParse(f[9], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out z)) return false;
            mFrames.Add(string.Join("|", f, 1, f.Length - 1));
            mTargets.Add(new int[] { x, y, z });
            mPathTypes.Add(path);
            if (mTargets.Count == LegsTotal) Phase = "Ready";
            return true;
        }
        public int[] Target(uint index)
        {
            if (index >= mTargets.Count) return null;
            return (int[])mTargets[(int)index].Clone();
        }
        public uint PathType(uint index) { return mPathTypes[(int)index]; }
        public byte DerivedKind(uint index)
        {
            if (mTargets.Count != LegsTotal || index >= LegsTotal) throw new FormatException("incomplete plan");
            byte[] kinds = new byte[mTargets.Count];
            kinds[kinds.Length - 1] = 4;
            for (int i = 0; i < kinds.Length; i++)
            {
                if ((mPathTypes[i] & 0x80000000u) == 0)
                {
                    if (mPathTypes[i] != 0) throw new FormatException("ordinary path type");
                    continue;
                }
                if (i < 1 || i > kinds.Length - 3 || kinds[i - 1] != 0 || kinds[i] != 0 || kinds[i + 1] != 0 ||
                    mPathTypes[i - 1] != 0 || mPathTypes[i + 1] != 0)
                    throw new FormatException("portal triple");
                kinds[i - 1] = 1; kinds[i] = 2; kinds[i + 1] = 3;
            }
            if (mPathTypes[kinds.Length - 1] != 0) throw new FormatException("handoff");
            return kinds[(int)index];
        }
        public bool CompleteLeg(uint index)
        {
            if (mTargets.Count != LegsTotal || index != (uint)mExecuted.Count || index >= LegsTotal)
            {
                InvalidProgressions++;
                return false;
            }
            mExecuted.Add(index);
            Phase = "Walking";
            return true;
        }
        public bool Settle(bool seated, int errorMm)
        {
            if (errorMm < 0 || errorMm > 100 || mExecuted.Count != LegsTotal || LegsTotal == 0) return false;
            Phase = seated ? "SeatingSettled" : "Halted";
            return true;
        }
        public string[] Frames() { return mFrames.ToArray(); }
        public uint[] ExecutedLegIndices() { return mExecuted.ToArray(); }
        public string Report()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            List<string> indices = new List<string>();
            foreach (uint index in mExecuted) indices.Add(index.ToString(c));
            return "corr=" + Correlation.ToString(c) + ";actor=" + Actor.ToString(c) +
                ";legs_total=" + LegsTotal.ToString(c) + ";received_legs=" + mTargets.Count.ToString(c) +
                ";executed_leg_indices=[" + string.Join(",", indices.ToArray()) + "];WalkPhase=" + Phase +
                ";invalid_progressions=" + InvalidProgressions.ToString(c);
        }

        // The shell and reloadable module share this memory through the shell assembly.
        // A reload never turns frame receipt into an execution receipt.
        public sealed class ReceiptStore
        {
            private readonly Dictionary<string, ClientBoundaryRoute> mRoutes = new Dictionary<string, ClientBoundaryRoute>();
            private readonly List<string> mOrder = new List<string>();
            private ulong mEpoch;
            private string mOpen;
            public ulong CurrentEpoch { get { return mEpoch; } }
            public bool Fenced;
            public string Open { get { return mOpen; } }
            private static string Key(ulong epoch, ulong corr, ulong actor)
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                return epoch.ToString(c) + "|" + corr.ToString(c) + "|" + actor.ToString(c);
            }
            public bool AdvanceEpoch(ulong epoch)
            {
                if (epoch == 0 || epoch < mEpoch) return false;
                if (epoch > mEpoch) { mRoutes.Clear(); mOrder.Clear(); mEpoch = epoch; }
                return true;
            }
            public bool Adopt(ulong epoch)
            {
                mOpen = null;
                Fenced = !AdvanceEpoch(epoch);
                return !Fenced;
            }
            public bool NavEpoch(string[] fields)
            {
                ulong epoch;
                if (fields == null || fields.Length != 3 || fields[0] != "0" || fields[1] != "Ne" ||
                    !TryNavEpoch(fields[2], out epoch))
                {
                    Fenced = true; mOpen = null;
                    return false;
                }
                return Adopt(epoch);
            }
            public bool Header(ulong epoch, string[] fields)
            {
                mOpen = null;
                if (epoch != mEpoch) return false;
                ClientBoundaryRoute route = new ClientBoundaryRoute();
                bool valid = route.OnPlanHeader(fields);
                ulong corr, actor;
                if (fields != null && fields.Length >= 4 && fields[1] == "Rt" &&
                    U64(fields[2], out corr) && U64(fields[3], out actor) && (!valid || (mEpoch != 0 && !Fenced)))
                {
                    string existing = Key(mEpoch, corr, actor);
                    if (mRoutes.Remove(existing))
                    {
                        mOrder.Remove(existing);
                        return false;
                    }
                }
                if (mEpoch == 0 || Fenced || !valid) return false;
                route.NavEpoch = epoch;
                string key = Key(epoch, route.Correlation, route.Actor);
                if (mRoutes.Count == 16) { mRoutes.Remove(mOrder[0]); mOrder.RemoveAt(0); }
                mRoutes.Add(key, route); mOrder.Add(key);
                mOpen = key;
                return true;
            }
            public bool Point(ulong epoch, string[] fields)
            {
                string open = mOpen;
                mOpen = null;
                ClientBoundaryRoute route;
                if (epoch != mEpoch || mEpoch == 0 || Fenced || open == null ||
                    !mRoutes.TryGetValue(open, out route) || !route.OnPoint(fields)) return false;
                if (route.mTargets.Count < route.LegsTotal) mOpen = open;
                return true;
            }
            public ClientBoundaryRoute Find(ulong corr, ulong actor)
            {
                return Find(mEpoch, corr, actor);
            }
            public ClientBoundaryRoute Find(ulong epoch, ulong corr, ulong actor)
            {
                ClientBoundaryRoute route;
                return mRoutes.TryGetValue(Key(epoch, corr, actor), out route) ? route : null;
            }
            public string Report(ulong corr, bool frames)
            {
                CultureInfo c = CultureInfo.InvariantCulture;
                string state = ";epoch=" + mEpoch.ToString(c) + ";fenced=" + (Fenced ? "1" : "0") +
                    ";open=" + (mOpen == null ? "0" : mRoutes[mOpen].Correlation.ToString(c));
                List<string> rows = new List<string>();
                foreach (string key in mOrder)
                {
                    ClientBoundaryRoute route = mRoutes[key];
                    if (corr != 0 && route.Correlation != corr) continue;
                    rows.Add(route.Report() + state);
                    if (frames) rows.AddRange(route.Frames());
                }
                string report = rows.Count == 0 ? "rust-route-client=missing" + state : string.Join("\n", rows.ToArray());
                // AUTONOMY_PROBE hex doubles the ASCII body in the 4080-byte carrier.
                // Large receipts require an agreed paging contract; never send a truncated plan as E3.
                return report.Length <= 1900 ? report : "rust-route-client=blocked;reason=receipt-exceeds-carrier";
            }
            private ClientBoundaryRoute Select(ulong corr, ulong actor, out string refusal)
            {
                refusal = "rust-route-client=missing";
                ClientBoundaryRoute found = null;
                foreach (ClientBoundaryRoute route in mRoutes.Values)
                {
                    if (route.Correlation != corr || (actor != 0 && route.Actor != actor)) continue;
                    if (found != null) { refusal = "rust-route-client=blocked;reason=ambiguous-actor"; return null; }
                    found = route;
                }
                return found;
            }
            public string ReportPlansPage(ulong corr, ulong actor, uint cursor, uint maxLines)
            {
                return ReportPlansPage(corr, actor, cursor, maxLines, null);
            }
            private string PageEpoch(uint cursor, ulong? epoch)
            {
                if (!epoch.HasValue && cursor > 0) return "rust-route-client=blocked;reason=epoch-required";
                if (epoch.HasValue && epoch.Value != mEpoch) return "rust-route-client=blocked;reason=epoch-mismatch";
                return null;
            }
            public string ReportPlansPage(ulong corr, ulong actor, uint cursor, uint maxLines, ulong? epoch)
            {
                string blocked = PageEpoch(cursor, epoch);
                if (blocked != null) return blocked;
                string refusal;
                ClientBoundaryRoute route = Select(corr, actor, out refusal);
                if (route == null) return refusal;
                if (cursor > route.mFrames.Count) return "rust-route-client=blocked;reason=cursor-out-of-range";
                uint count = Math.Min(Math.Max(maxLines, 1u), 16u);
                count = Math.Min(count, (uint)route.mFrames.Count - cursor);
                CultureInfo c = CultureInfo.InvariantCulture;
                string header = "rust-route-plans=page;epoch=" + mEpoch.ToString(c) + ";corr=" + corr.ToString(c) + ";actor=" + route.Actor.ToString(c) +
                    ";cursor=" + cursor.ToString(c) + ";count=" + count.ToString(c) + ";total=" + route.mFrames.Count.ToString(c) +
                    ";next=" + (cursor + count).ToString(c) + ";more=" + (cursor + count < route.mFrames.Count ? "1" : "0");
                System.Text.StringBuilder body = new System.Text.StringBuilder(header);
                for (uint i = 0; i < count; i++) body.Append('\n').Append(route.mFrames[(int)(cursor + i)]);
                if (body.Length > 1900) throw new FormatException("page carrier");
                return body.ToString();
            }
            public string ReportClientPage(ulong corr, ulong actor, uint cursor, uint maxItems)
            {
                return ReportClientPage(corr, actor, cursor, maxItems, null);
            }
            public string ReportClientPage(ulong corr, ulong actor, uint cursor, uint maxItems, ulong? epoch)
            {
                string blocked = PageEpoch(cursor, epoch);
                if (blocked != null) return blocked;
                string refusal;
                ClientBoundaryRoute route = Select(corr, actor, out refusal);
                if (route == null) return refusal;
                if (cursor > route.mExecuted.Count) return "rust-route-client=blocked;reason=cursor-out-of-range";
                uint count = Math.Min(Math.Max(maxItems, 1u), 128u);
                count = Math.Min(count, (uint)route.mExecuted.Count - cursor);
                CultureInfo c = CultureInfo.InvariantCulture;
                List<string> indices = new List<string>();
                for (uint i = 0; i < count; i++) indices.Add(route.mExecuted[(int)(cursor + i)].ToString(c));
                string body = "rust-route-client=page;epoch=" + mEpoch.ToString(c) + ";corr=" + corr.ToString(c) + ";actor=" + route.Actor.ToString(c) +
                    ";legs_total=" + route.LegsTotal.ToString(c) + ";received_legs=" + route.mTargets.Count.ToString(c) +
                    ";executed_count=" + route.mExecuted.Count.ToString(c) + ";cursor=" + cursor.ToString(c) +
                    ";count=" + count.ToString(c) + ";next=" + (cursor + count).ToString(c) +
                    ";more=" + (cursor + count < route.mExecuted.Count ? "1" : "0") + ";WalkPhase=" + route.Phase +
                    ";invalid_progressions=" + route.InvalidProgressions.ToString(c) +
                    ";executed_slice=[" + string.Join(",", indices.ToArray()) + "]";
                if (body.Length > 1900) throw new FormatException("page carrier");
                return body;
            }
            public void Clear() { mRoutes.Clear(); mOrder.Clear(); mEpoch = 0; Fenced = false; mOpen = null; }
        }
    }
}
