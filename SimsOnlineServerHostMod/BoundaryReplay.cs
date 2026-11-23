using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Client side of the generated engine-boundary recorders (scripts/gen_boundary_recorder.py): a line
    // <kind>|object|Method:codes|args... is the same call on this client's copy, through this game's own implementation of
    // that interface, found by name and argument types. Nothing here knows any one method: a setter added to a recorder's
    // configuration replays without a change here. A lot-keyed setter (<lot kind>|lot|Method:codes|args, Wl: the floor
    // features) takes the lot id first instead of an object; lots load with the same id on every game, so it is not mapped.
    internal static class BoundaryReplay
    {
        internal sealed class Target
        {
            public readonly Type Interface;
            public readonly FieldInfo Holder;
            public object Instance;
            public readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();
            public long Applied;
            public long Failed;
            public long Walking;
            public long LotApplied;
            public long OtherApplied; // calls other than placements (a scenario asserts a setter reached the client)
            public string LastOther = "-";
            public string LastLot = "-"; // the last lot-keyed method applied (Wl): scenarios burnt-floor, holiday-lights

            public Target(Type iface, FieldInfo holder)
            {
                Interface = iface;
                Holder = holder;
            }
        }

        internal static readonly Target World = new Target(typeof(IWorld), WorldRecorder.HolderField());
        internal static readonly Target Objects = new Target(typeof(IObjects), ObjectsRecorder.HolderField());

        // Placement calls a walking copy leaves to its walk (the client route moves it along the server's path).
        private static bool IsPlacement(string name)
        {
            return name == "SetPosition" || name == "SetTransform" || name == "SetForward" || name == "SetRotation" || name == "ObjectSetPosition";
        }

        private static bool Matches(Type type, char code)
        {
            switch (code)
            {
                case 'g': return type == typeof(ObjectGuid);
                case 's': return type == typeof(string);
                case 'f': return type == typeof(float);
                case 'b': return type == typeof(bool);
                case 'u': return type == typeof(uint);
                case 'i': return type == typeof(int);
                case 'h': return type == typeof(ushort);
                case 'l': return type == typeof(LotLocation);
                case 'k': return type == typeof(ResourceKey);
                case 'F': return type == typeof(float[]);
                case 'v': return type == typeof(Vector3);
                case 'm': return type == typeof(Matrix44);
                case 'e': return type.IsEnum;
            }

            return false;
        }

        private static MethodInfo Find(Target target, string name, string codes, bool lot)
        {
            string tag = (lot ? "lot:" : "") + name + ":" + codes;
            MethodInfo found;
            if (target.Methods.TryGetValue(tag, out found))
            {
                return found;
            }

            foreach (MethodInfo method in target.Interface.GetMethods())
            {
                ParameterInfo[] ps = method.GetParameters();
                if (method.Name != name || ps.Length != codes.Length + 1 || ps[0].ParameterType != (lot ? typeof(ulong) : typeof(ObjectGuid)))
                {
                    continue;
                }

                bool ok = true;
                for (int i = 0; i < codes.Length && ok; i++)
                {
                    ok = Matches(ps[i + 1].ParameterType, codes[i]);
                }

                if (ok)
                {
                    found = method;
                    break;
                }
            }

            target.Methods[tag] = found;
            return found;
        }

        private static Vector4 V4(string[] p, int at)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            Vector4 v = new Vector4();
            v.x = float.Parse(p[at], c);
            v.y = float.Parse(p[at + 1], c);
            v.z = float.Parse(p[at + 2], c);
            v.w = float.Parse(p[at + 3], c);
            return v;
        }

        private static object Parse(string text, char code, Type type, Converter<ulong, ObjectGuid> alias)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            switch (code)
            {
                case 'g': return alias(ulong.Parse(text, c));
                case 's': return BridgeCodec.DecodeText(text);
                case 'f': return float.Parse(text, c);
                case 'b': return text == "1";
                case 'u': return uint.Parse(text, c);
                case 'i': return int.Parse(text, c);
                case 'h': return ushort.Parse(text, c);
                case 'l':
                    {
                        string[] p = text.Split('~');
                        return new LotLocation(int.Parse(p[0], c), int.Parse(p[1], c), sbyte.Parse(p[2], c), short.Parse(p[3], c));
                    }
                case 'k':
                    {
                        string[] p = text.Split('~');
                        return new ResourceKey(ulong.Parse(p[0], c), uint.Parse(p[1], c), uint.Parse(p[2], c));
                    }
                case 'F':
                    {
                        if (text == "-")
                        {
                            return new float[0];
                        }

                        string[] p = text.Split(';');
                        float[] values = new float[p.Length];
                        for (int i = 0; i < p.Length; i++)
                        {
                            values[i] = float.Parse(p[i], c);
                        }

                        return values;
                    }
                case 'v':
                    {
                        string[] p = text.Split('~');
                        return new Vector3(float.Parse(p[0], c), float.Parse(p[1], c), float.Parse(p[2], c));
                    }
                case 'm':
                    {
                        string[] p = text.Split('~');
                        return new Matrix44(V4(p, 0), V4(p, 4), V4(p, 8), V4(p, 12));
                    }
                case 'e': return Enum.ToObject(type, long.Parse(text, c)); // recorders send enums as long (a uint flag enum)
            }

            throw new FormatException("code " + code);
        }

        // f: seq|kind|lot|Method:codes|args... for a lot-keyed setter (Wl).
        internal static bool ReplayLot(Target target, string[] f, Converter<ulong, ObjectGuid> alias)
        {
            if (f.Length < 4)
            {
                return false;
            }

            try
            {
                if (target.Instance == null && target.Holder != null)
                {
                    target.Instance = target.Holder.GetValue(null);
                }

                ulong lot = ulong.Parse(f[2], CultureInfo.InvariantCulture);
                int colon = f[3].IndexOf(':');
                if (target.Instance == null || lot == 0uL || colon < 0)
                {
                    return false;
                }

                string codes = f[3].Substring(colon + 1);
                MethodInfo method = Find(target, f[3].Substring(0, colon), codes, true);
                if (method == null || f.Length < 4 + codes.Length)
                {
                    target.Failed++;
                    return false;
                }

                ParameterInfo[] ps = method.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = lot;
                for (int i = 0; i < codes.Length; i++)
                {
                    args[i + 1] = Parse(f[4 + i], codes[i], ps[i + 1].ParameterType, alias);
                }

                method.Invoke(target.Instance, args);
                target.Applied++;
                target.LotApplied++;
                target.LastLot = method.Name;
                return true;
            }
            catch (Exception)
            {
                target.Failed++;
                return false;
            }
        }

        // f: seq|kind|object|Method:codes|args... ; walking: whether this client's copy (local id) is walking a client route.
        internal static bool Replay(Target target, string[] f, Converter<ulong, ObjectGuid> alias, Predicate<ulong> walking)
        {
            if (f.Length < 4)
            {
                return false;
            }

            try
            {
                if (target.Instance == null && target.Holder != null)
                {
                    target.Instance = target.Holder.GetValue(null);
                }

                ObjectGuid obj = alias(ulong.Parse(f[2], CultureInfo.InvariantCulture));
                int colon = f[3].IndexOf(':');
                if (target.Instance == null || !obj.IsValid || colon < 0)
                {
                    return false;
                }

                string name = f[3].Substring(0, colon);
                string codes = f[3].Substring(colon + 1);
                if (IsPlacement(name) && walking != null && walking(obj.Value))
                {
                    target.Walking++; // the walk on the server's path places it; where the server's ended comes with Rx
                    return true;
                }

                MethodInfo method = Find(target, name, codes, false);
                if (method == null || f.Length < 4 + codes.Length)
                {
                    target.Failed++;
                    return false;
                }

                ParameterInfo[] ps = method.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = obj;
                for (int i = 0; i < codes.Length; i++)
                {
                    args[i + 1] = Parse(f[4 + i], codes[i], ps[i + 1].ParameterType, alias);
                }

                method.Invoke(target.Instance, args);
                target.Applied++;
                if (!IsPlacement(name))
                {
                    target.OtherApplied++;
                    target.LastOther = name;
                }

                return true;
            }
            catch (Exception)
            {
                target.Failed++;
                return false;
            }
        }

        internal static string Report(Target target)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "applied:" + target.Applied.ToString(c) + "/failed:" + target.Failed.ToString(c) + "/walking:" + target.Walking.ToString(c)
                + "/lots:" + target.LotApplied.ToString(c) + "/other:" + target.OtherApplied.ToString(c) + "/lastOther:" + target.LastOther + "/lastLot:" + target.LastLot;
        }
    }
}
