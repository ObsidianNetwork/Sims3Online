// C#2 ONLY. This file is compiled two ways and must stay valid under BOTH:
//   1. The Sims 3 script mod, by csc.exe 2.0 (top-level *.cs glob over this directory: keep it in the mod ROOT).
//   2. The .NET 8 test project (DedicatedServer.Tests), source-linked via <Compile Include="..">.
// Therefore: NO var, NO LINQ, NO lambdas, NO C#3+ syntax, NO engine types.
using System;

namespace SimsOnlineServerHost
{
    // B1: which caller ran ServerHostMain.RepairOwnMachines, in the label the motion watch prints after "by"
    // (SacsRecorder.LocalContext; scripts/motion_watch.py attributes a client restart to it). The callers: the batch end
    // after a replayed Z (repair:z), a standing Po (repair:po, inside the replay batch) and the module's client walk
    // (repair:walk, PresentationApi.RepairOwnMachines). A repair label is a prefix: B2 extends it with the entry it chose
    // (repair:z/stream, repair:po/default). Runs from before 2026-11-01 say plain "repair".
    internal static class RepairLabel
    {
        internal const string Plain = "repair";
        internal const string BatchEnd = "repair:z";
        internal const string Posture = "repair:po";
        internal const string Walk = "repair:walk";

        // The context RepairOwnMachines runs under: the caller's repair label kept, any other context (or none) the plain one.
        internal static string For(string callerContext)
        {
            return IsRepair(callerContext) ? callerContext : Plain;
        }

        // A repair label: plain, or with a caller (repair:...) or an entry kind (repair/...) after it.
        internal static bool IsRepair(string context)
        {
            return context != null && (context == Plain || context.StartsWith(Plain + ":", StringComparison.Ordinal)
                || context.StartsWith(Plain + "/", StringComparison.Ordinal));
        }
    }
}
