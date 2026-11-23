// PURE C#2: source-linked into the controller tests. No engine types.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SimsOnlineServerHost
{
    public static class RustRoadPlace
    {
        public sealed class Answer
        {
            public bool Found;
            public float X, Y, Z, Qx, Qy, Qz, Qw;
            public uint LaneId;
            public bool Onscreen;
            public string Hash;
        }

        private static bool sEnabled;
        private static readonly SortedDictionary<string, long> sCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        private static string sLastHash = "-";
        private static readonly string[] sDecisionNames = new string[] { "off", "fallback.unmappedOption",
            "fallback.inPump", "fallback.notYielding.road", "fallback.notYielding.onFoot", "fallback.noDaemon",
            "fallback.timeout", "fallback.RoadDataMissing", "fallback.LaneKindUnsupported", "fallback.BadRequest",
            "served", "servedNone" };
        public static RustRoutePlan.LineSender RequestSender;

        public static bool Enabled
        {
            get { return sEnabled; }
            set { sEnabled = value; RustBridgeWait.Cancel("road"); }
        }

        public static void Count(string name)
        {
            long n;
            sCounts.TryGetValue(name, out n);
            sCounts[name] = n + 1;
        }

        public static long Counter(string name)
        {
            long n;
            return sCounts.TryGetValue(name, out n) ? n : 0L;
        }

        private static Answer Fallback(int option, string reason)
        {
            Count("fallback." + reason);
            Count("option." + option.ToString(CultureInfo.InvariantCulture) + "." + reason);
            return null;
        }

        // null means native fallback; Found=false is an accepted answer, never a fallback.
        public static Answer RequestAndWait(ulong obj, float x, float y, float z, int option, float distance,
            int radiusCm, string view, double seconds, RustRoutePlan.IWaitEnv env)
        {
            if (!sEnabled) { Count("off"); return null; }
            if (option < 0 || option > 5) return Fallback(option, "unmappedOption");
            if (RustRoutePlan.InBridgePump) return Fallback(option, "inPump");
            if (env == null || !env.CanYield)
            {
                return Fallback(option, option == 0 ? "notYielding.road" : "notYielding.onFoot");
            }
            string reason;
            Answer answer;
            try
            {
                answer = RustBridgeWait.Request("road",
                    delegate(ulong corr) { return BridgeLine(corr, obj, x, y, z, option, distance, radiusCm, view); },
                    RequestSender, seconds, env, out reason) as Answer;
            }
            catch (Exception e)
            {
                if (RustRoutePlan.MustRethrow(e)) throw;
                return Fallback(option, "noDaemon");
            }
            if (answer == null)
            {
                return Fallback(option, reason == null ? "noDaemon" : reason);
            }
            sLastHash = answer.Hash ?? "-";
            Count(answer.Found ? "served" : "servedNone");
            return answer;
        }

        public static string BridgeLine(ulong corr, ulong obj, float x, float y, float z, int option,
            float distance, int radiusCm, string view)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "EVENT|RoadPlaceRequest|" + corr.ToString(c) + "|" + obj.ToString(c) + "|"
                + Centimetres(x).ToString(c) + "|" + Centimetres(y).ToString(c) + "|" + Centimetres(z).ToString(c) + "|"
                + option.ToString(c) + "|" + Centimetres(distance).ToString(c) + "|"
                + (obj == 0uL ? 0 : radiusCm).ToString(c) + "|" + (view ?? "none");
        }

        public static int Centimetres(float metres)
        {
            return checked((int)Math.Round(metres * 100f));
        }

        public static Answer FromHeading(int x, int y, int z, int heading, uint lane, bool onscreen, string hash)
        {
            Answer a = new Answer();
            a.Found = true;
            a.X = x / 100f; a.Y = y / 100f; a.Z = z / 100f;
            double halfYaw = heading * Math.PI / 360000.0;
            a.Qy = (float)Math.Sin(halfYaw); a.Qw = (float)Math.Cos(halfYaw);
            a.LaneId = lane; a.Onscreen = onscreen; a.Hash = hash;
            return a;
        }

        public static bool Offer(ulong corr, Answer answer)
        {
            bool accepted = sEnabled && answer != null && RustBridgeWait.Offer("road", corr, answer, null);
            if (!accepted) Count("refused");
            return accepted;
        }

        public static bool OfferNone(ulong corr, string hash)
        {
            Answer a = new Answer();
            a.Hash = hash;
            return Offer(corr, a);
        }

        public static bool Decline(ulong corr, string reason)
        {
            return sEnabled && RustBridgeWait.Offer("road", corr, null, string.IsNullOrEmpty(reason) ? "noDaemon" : reason);
        }

        // Parts start at the corr; accepts cm + heading or metre position + quaternion, plus census metadata.
        public static bool TryParseAnswer(string[] parts, int start, out ulong corr, out Answer answer)
        {
            corr = 0uL; answer = null;
            int count = parts.Length - start;
            if (count < 5 || !ulong.TryParse(parts[start], NumberStyles.None, CultureInfo.InvariantCulture, out corr) || corr == 0uL)
                return false;
            CultureInfo c = CultureInfo.InvariantCulture;
            if (count == 5)
            {
                int x, y, z, heading;
                if (!int.TryParse(parts[start + 1], NumberStyles.AllowLeadingSign, c, out x)
                    || !int.TryParse(parts[start + 2], NumberStyles.AllowLeadingSign, c, out y)
                    || !int.TryParse(parts[start + 3], NumberStyles.AllowLeadingSign, c, out z)
                    || !int.TryParse(parts[start + 4], NumberStyles.None, c, out heading) || heading >= 360000)
                    return false;
                answer = FromHeading(x, y, z, heading, 0u, false, null);
                return true;
            }
            if (count < 8 || count > 11) return false;
            float[] v = new float[7];
            for (int i = 0; i < v.Length; i++)
            {
                if (!float.TryParse(parts[start + 1 + i], NumberStyles.Float, c, out v[i])
                    || float.IsNaN(v[i]) || float.IsInfinity(v[i])) return false;
            }
            Answer a = new Answer();
            a.Found = true;
            a.X = v[0]; a.Y = v[1]; a.Z = v[2]; a.Qx = v[3]; a.Qy = v[4]; a.Qz = v[5]; a.Qw = v[6];
            if (count >= 9 && !uint.TryParse(parts[start + 8], NumberStyles.None, c, out a.LaneId)) return false;
            if (count >= 10)
            {
                if (parts[start + 9] != "0" && parts[start + 9] != "1") return false;
                a.Onscreen = parts[start + 9] == "1";
            }
            if (count == 11) a.Hash = parts[start + 10];
            answer = a;
            return true;
        }

        public static string DescribeStatus()
        {
            StringBuilder b = new StringBuilder("roadPlace:");
            b.Append(sEnabled ? "on" : "off");
            for (int i = 0; i < sDecisionNames.Length; i++)
            {
                string name = sDecisionNames[i];
                b.Append(',').Append(name).Append(':').Append(Counter(name).ToString(CultureInfo.InvariantCulture));
            }
            foreach (KeyValuePair<string, long> pair in sCounts)
            {
                if (Array.IndexOf(sDecisionNames, pair.Key) < 0)
                    b.Append(',').Append(pair.Key).Append(':').Append(pair.Value.ToString(CultureInfo.InvariantCulture));
            }
            b.Append(",lastHash:").Append(sLastHash);
            return b.ToString();
        }

        public static void Reset()
        {
            Enabled = false;
            RequestSender = null;
            sCounts.Clear();
            sLastHash = "-";
        }
    }
}
