using SimsOnline.Presentation;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("JoinSettlePlan.PendingJoinNeedsPlaybackAndBothCopiesStill")]
    private static void JoinSettlePlanPendingJoinNeedsPlaybackAndBothCopiesStill()
    {
        TimeSpan settle = TimeSpan.FromSeconds(1.5);
        TimeSpan hold = TimeSpan.FromSeconds(3);
        TimeSpan shortSettle = settle - TimeSpan.FromTicks(1);
        TimeSpan shortHold = hold - TimeSpan.FromTicks(1);
        Assert(JoinSettlePlan.ShouldSettle(true, true, false, false, settle, settle, hold, settle, hold),
            "An in-place join is consumed at the exact stillness and animation boundaries.");
        Assert(!JoinSettlePlan.ShouldSettle(true, false, false, false, settle, settle, hold, settle, hold),
            "Wall time alone cannot consume history before simulator playback.");
        Assert(!JoinSettlePlan.ShouldSettle(true, true, false, false, shortSettle, settle, hold, settle, hold),
            "The copy still moving holds the join open even when already in place.");
        Assert(!JoinSettlePlan.ShouldSettle(true, true, false, false, settle, shortSettle, hold, settle, hold),
            "The server still moving holds the join open.");
        Assert(!JoinSettlePlan.ShouldSettle(true, true, false, false, settle, settle, shortHold, settle, hold),
            "A recent streamed animation holds the join open.");
    }

    [Test("JoinSettlePlan.NonSeatDriftSettlesAfterConsumedJoin")]
    private static void JoinSettlePlanNonSeatDriftSettlesAfterConsumedJoin()
    {
        TimeSpan settle = TimeSpan.FromSeconds(1.5);
        TimeSpan hold = TimeSpan.FromSeconds(3);
        // The initial settle can finish before a long entry clip's root motion does.
        Assert(JoinSettlePlan.ShouldSettle(true, true, true, true, settle, settle, hold, settle, hold),
            "The first correction can settle an on-object join.");
        Assert(!JoinSettlePlan.ShouldSettle(false, true, true, true, TimeSpan.Zero, settle, hold, settle, hold),
            "Further root motion must finish before the correction repeats.");
        Assert(JoinSettlePlan.ShouldSettle(false, true, true, true, settle, settle, hold, settle, hold),
            "Position or facing drift on a non-seat object still settles after the join was consumed.");
        Assert(!JoinSettlePlan.ShouldSettle(false, false, true, true, settle, settle, hold, settle, hold),
            "Repeat correction also requires the simulator playback gate.");
        Assert(!JoinSettlePlan.ShouldSettle(false, true, true, true, settle, TimeSpan.Zero, hold, settle, hold),
            "Repeat correction cannot race the server's movement.");
        Assert(!JoinSettlePlan.ShouldSettle(false, true, true, true, settle, settle, TimeSpan.Zero, settle, hold),
            "Repeat correction cannot cut a newly streamed exit clip short.");
    }

    [Test("JoinSettlePlan.NoRepeatForSeatStandingOrAlignedObject")]
    private static void JoinSettlePlanNoRepeatForSeatStandingOrAlignedObject()
    {
        TimeSpan settle = TimeSpan.FromSeconds(1.5);
        TimeSpan hold = TimeSpan.FromSeconds(3);
        Assert(!JoinSettlePlan.ShouldSettle(false, true, false, true, settle, settle, hold, settle, hold),
            "Seats keep their one-shot join settle and standing copies keep the ordinary mirror.");
        Assert(!JoinSettlePlan.ShouldSettle(false, true, true, false, settle, settle, hold, settle, hold),
            "An aligned non-seat object needs no repeat correction.");
    }
}
