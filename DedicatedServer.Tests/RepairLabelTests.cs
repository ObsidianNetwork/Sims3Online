using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// B1: RepairOwnMachines runs under its caller's label (SacsRecorder.LocalContext, printed after "by" in the
/// motion watch), so a client restart after a repair names the caller: the batch end after a Z, a standing Po, a walk.
/// </summary>
internal static partial class Program
{
    [Test("RepairLabel.CallersKeepTheirLabel")]
    private static void RepairLabelCallersKeepTheirLabel()
    {
        Assert(RepairLabel.BatchEnd == "repair:z" && RepairLabel.Posture == "repair:po" && RepairLabel.Walk == "repair:walk",
            "The caller labels scripts/motion_watch.py reads (repair:<caller>).");
        Assert(RepairLabel.For(RepairLabel.BatchEnd) == RepairLabel.BatchEnd, "The batch end's label is kept.");
        Assert(RepairLabel.For(RepairLabel.Posture) == RepairLabel.Posture, "A standing Po's label is kept.");
        Assert(RepairLabel.For(RepairLabel.Walk) == RepairLabel.Walk, "A walk's label is kept.");
        Assert(RepairLabel.For("repair:z/stream") == "repair:z/stream" && RepairLabel.For("repair/default") == "repair/default",
            "A label B2 extends with its entry kind is kept whole.");
    }

    [Test("RepairLabel.OtherContextsArePlain")]
    private static void RepairLabelOtherContextsArePlain()
    {
        Assert(RepairLabel.For(null) == "repair", "No caller: the plain label older runs print.");
        Assert(RepairLabel.For("stand-up") == "repair" && RepairLabel.For("presenter posture (Po)") == "repair",
            "Another mod path's context is not a repair caller.");
        Assert(RepairLabel.For("repairs") == "repair" && !RepairLabel.IsRepair("repairs") && !RepairLabel.IsRepair("replay"),
            "Only repair, repair:... and repair/... are repair labels.");
        Assert(RepairLabel.IsRepair("repair") && RepairLabel.IsRepair(RepairLabel.Posture),
            "Inside the batch a repair label is the mod's own entry, not the replay's (SacsRecorder's motion note).");
    }
}
