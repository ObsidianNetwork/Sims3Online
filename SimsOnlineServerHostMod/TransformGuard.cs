using System;
using System.Globalization;
using Sims3.SimIFace;

namespace SimsOnlineServerHost
{
    // Client side: after a streamed object transform is applied (Ob|object|SetTransform:m, by the replay or by the
    // playout buffer), check that the object still has a valid facing. In two-lots-travel-stability (runs 14, 18, 19
    // and 21, 2026-11-15) the taxi's copy read a valid position and an invalid (NaN) forward on both clients for 50 to
    // 270 ms, right after transforms whose rotation was within about 1e-6 of straight, and the Sim seated in it read
    // NaN with it. Nothing invalid was in the lines sent, so the game's own SetTransform produced it. The guard puts a
    // valid facing back: first with SetForward from the transform's own forward axis, then with a level transform
    // built from that axis. Each case is counted and noted for the motion watch, so a run says which one was needed.
    internal static class TransformGuard
    {
        internal static int Checked;
        internal static int Bad;
        internal static int BySetForward;
        internal static int ByLevel;
        internal static int Unrepaired;
        internal static int Errors;

        internal static string Report()
        {
            return "checked=" + Checked.ToString(CultureInfo.InvariantCulture) + ";bad=" + Bad.ToString(CultureInfo.InvariantCulture)
                + ";by_set_forward=" + BySetForward.ToString(CultureInfo.InvariantCulture) + ";by_level=" + ByLevel.ToString(CultureInfo.InvariantCulture)
                + ";unrepaired=" + Unrepaired.ToString(CultureInfo.InvariantCulture) + ";errors=" + Errors.ToString(CultureInfo.InvariantCulture);
        }

        private static IObjects Target()
        {
            BoundaryReplay.Target target = BoundaryReplay.Objects;
            if (target.Instance == null && target.Holder != null)
            {
                target.Instance = target.Holder.GetValue(null);
            }

            return target.Instance as IObjects;
        }

        private static bool Invalid(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
        }

        // After the replay applied f (kind|seq|object|SetTransform:m|matrix): the matrix is parsed only when the facing
        // came out invalid.
        internal static void AfterLine(string[] f, Converter<ulong, ObjectGuid> alias)
        {
            try
            {
                if (f == null || f.Length < 5 || f[3] != "SetTransform:m" || alias == null)
                {
                    return;
                }

                IObjects objects = Target();
                ObjectGuid obj = alias(ulong.Parse(f[2], CultureInfo.InvariantCulture));
                if (objects == null || !obj.IsValid)
                {
                    return;
                }

                Checked++;
                if (!Invalid(objects.GetForward(obj)))
                {
                    return;
                }

                string[] p = f[4].Split('~');
                if (p.Length < 16)
                {
                    Bad++;
                    Unrepaired++;
                    return;
                }

                float[] v = new float[16];
                for (int i = 0; i < 16; i++)
                {
                    v[i] = float.Parse(p[i], CultureInfo.InvariantCulture);
                }

                Repair(objects, obj, new Matrix44(new Vector4(v[0], v[1], v[2], v[3]), new Vector4(v[4], v[5], v[6], v[7]),
                    new Vector4(v[8], v[9], v[10], v[11]), new Vector4(v[12], v[13], v[14], v[15])));
            }
            catch (Exception)
            {
                Errors++;
            }
        }

        // After a caller applied this transform itself (the playout buffer).
        internal static void After(IObjects objects, ObjectGuid obj, Matrix44 transform)
        {
            try
            {
                if (objects == null)
                {
                    return;
                }

                Checked++;
                if (Invalid(objects.GetForward(obj)))
                {
                    Repair(objects, obj, transform);
                }
            }
            catch (Exception)
            {
                Errors++;
            }
        }

        private static void Repair(IObjects objects, ObjectGuid obj, Matrix44 m)
        {
            Bad++;
            string how = "not repaired";
            string context = SacsRecorder.LocalContext;
            SacsRecorder.LocalContext = "transform guard";
            try
            {
                double x = m.at.x, y = m.at.y, z = m.at.z;
                double length = Math.Sqrt(x * x + y * y + z * z);
                double flat = Math.Sqrt(x * x + z * z);
                if (!(length > 1e-6) || double.IsNaN(length) || double.IsInfinity(length))
                {
                    Unrepaired++;
                    how = "not repaired (the transform has no forward axis)";
                }
                else
                {
                    objects.SetForward(obj, new Vector3((float)(x / length), (float)(y / length), (float)(z / length)));
                    if (!Invalid(objects.GetForward(obj)))
                    {
                        BySetForward++;
                        how = "repaired by SetForward";
                    }
                    else if (flat > 1e-6)
                    {
                        // A level transform facing the same way: exact zeros and ones except the heading.
                        float fx = (float)(x / flat), fz = (float)(z / flat);
                        objects.SetTransform(obj, new Matrix44(new Vector4(fz, 0f, -fx, 0f), new Vector4(0f, 1f, 0f, 0f),
                            new Vector4(fx, 0f, fz, 0f), m.pos));
                        if (!Invalid(objects.GetForward(obj)))
                        {
                            ByLevel++;
                            how = "repaired by a level transform";
                        }
                        else
                        {
                            Unrepaired++;
                        }
                    }
                    else
                    {
                        Unrepaired++;
                    }
                }
            }
            catch (Exception)
            {
                Errors++;
                how = "repair threw";
            }

            SacsRecorder.LocalContext = context;
            string note = "transform guard: " + obj.Value.ToString(CultureInfo.InvariantCulture) + " facing INVALID after SetTransform; " + how
                + " (at " + m.at.x.ToString("R", CultureInfo.InvariantCulture) + "," + m.at.y.ToString("R", CultureInfo.InvariantCulture) + ","
                + m.at.z.ToString("R", CultureInfo.InvariantCulture) + ")";
            MotionWatch.NoteApplied(note);
            MotionWatch.NoteEvery(note);
        }
    }
}
