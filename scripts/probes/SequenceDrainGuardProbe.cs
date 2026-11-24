using System;
using System.Reflection;

namespace SimsOnline.Probes
{
    public class SequenceDrainGuardProbe
    {
        public static string Run()
        {
            Type shadow = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (shadow == null) shadow = assembly.GetType("SimsOnlineServerHost.SimLinkShadow");
            if (shadow == null) return "ok=false;error=no sidecar";
            BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            int version = (int)shadow.GetProperty("NativeVersion", flags).GetValue(null, null);
            if (version < 17) return "ok=false;native=" + version;
            MethodInfo counter = shadow.GetMethod("SequenceDrain", flags);
            int state = (int)counter.Invoke(null, new object[] { 0 });
            int checks = (int)counter.Invoke(null, new object[] { 1 });
            int prevented = (int)counter.Invoke(null, new object[] { 2 });
            return "ok=" + (state == 1 && checks > 0 ? "true" : "false")
                + ";native=" + version + ";state=" + state + ";checks=" + checks + ";prevented=" + prevented;
        }
    }
}
