using System.Globalization;

namespace SimsOnlineServerHost
{
    // Errors the mod swallows: the empty catch blocks that keep a game hook, the bridge pump or a report going.
    // DIAG_STATE reports the count as swallowed=N; a count that keeps rising means one of them hides a fault.
    // This runs inside catch blocks, and from Initializer's static constructor (ServerHostMain.Initialise), so:
    // nothing here may throw, there is no static constructor and no field initializer, and no game type is touched.
    // No lock: a lost increment, if two threads ever note at once, only makes the count low.
    // PURE: source-linked into DedicatedServer.Tests (SwallowedErrors.CountForDiagState).
    internal static class SwallowedErrors
    {
        internal static int Count;

        internal static void Note()
        {
            if (Count < int.MaxValue)
            {
                Count++;
            }
        }

        // The DIAG_STATE field.
        internal static string DiagStateField()
        {
            return "swallowed=" + Count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
