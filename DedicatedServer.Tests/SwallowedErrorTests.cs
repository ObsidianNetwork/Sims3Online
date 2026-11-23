using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>The mod's swallowed-error counter (SwallowedErrors, source-linked) and its DIAG_STATE field.</summary>
internal static partial class Program
{
    [Test("SwallowedErrors.CountForDiagState")]
    private static void SwallowedErrorsCountForDiagState()
    {
        int start = SwallowedErrors.Count;
        try
        {
            SwallowedErrors.Note();
            SwallowedErrors.Note();
            Assert(SwallowedErrors.Count == start + 2, "Each note should count once.");
            Assert(SwallowedErrors.DiagStateField() == "swallowed=" + (start + 2), "The DIAG_STATE field is swallowed=<count>: " + SwallowedErrors.DiagStateField());

            // DIAG_STATE as HandleDiagState builds it in the world, the new field last: the controller's parsers still read
            // the rest (scripts/diag_state_readers_test.py does the same for the scripts).
            string report = "role=server;inWorld=True;tick=4521;autonomyWatch=(none);commandGate=(none);pendingOutbound=0;pumpTicks=812"
                + ";probeLoads=0;probeReuses=0;dispatch=63;commandedSpeed=Normal;observedSpeed=Normal;clockDay=2;clockHours=9"
                + ";clockMinutes=41;clockTicks=98765;clockSets=1/0;clockLastSetMin=0.5;hudClockDueMin=0.73"
                + ";inventories=2/14/3/0/0/0/polls:40/reads:2.5/ms:0.012,0.3/findMs:0.004,0.1"
                + ";uses=1/6/2/0/0/polls:40/ms:0.008,0.2/last:Chair:1,0;refusals=0/sent:0/cleared:0/skipped:0/retests:0/errors:0;"
                + SwallowedErrors.DiagStateField();
            Assert(SyncBroadcaster.TryParseDiagInWorld(report), "inWorld should still parse.");
            Assert(SyncBroadcaster.TryParseDiagTick(report, out long tick) && tick == 4521L, "tick should still parse.");
            Assert(WorldTimeReconcilePolicy.TryParseDiagClock(report, out WorldTime time) && time == new WorldTime(2, 9, 41), "The clock should still parse.");

            // Never wraps (and never throws inside a catch).
            SwallowedErrors.Count = int.MaxValue;
            SwallowedErrors.Note();
            Assert(SwallowedErrors.Count == int.MaxValue, "The count should stop at int.MaxValue.");
        }
        finally
        {
            SwallowedErrors.Count = start;
        }
    }
}
