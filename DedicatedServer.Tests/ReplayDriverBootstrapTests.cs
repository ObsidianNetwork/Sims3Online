using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("ReplayDriverBootstrap.EntersStandingBeforePublishingOwner")]
    private static void ReplayDriverBootstrapEntersStandingBeforePublishingOwner()
    {
        string main = File.ReadAllText(Path.Combine(SolutionRoot(), "SimsOnlineServerHostMod", "ServerHostMain.cs"));
        int start = main.IndexOf("private static uint BootstrapStandingIdle(Sim sim)", StringComparison.Ordinal);
        int end = main.IndexOf("private static uint OwnDriverHandle(", start, StringComparison.Ordinal);
        Assert(start >= 0 && end > start, "The shared idle bootstrap exists.");
        string body = main.Substring(start, end - start);
        int bind = body.IndexOf("sm.SetActor(\"x\", sim);", StringComparison.Ordinal);
        int enter = body.IndexOf("sm.EnterState(\"x\", \"standing\");", StringComparison.Ordinal);
        int publish = body.IndexOf("standing.CurrentStateMachine = sm;", StringComparison.Ordinal);
        Assert(bind >= 0 && enter > bind && publish > enter,
            "Like the engine's standing-idle creation, bind and enter standing before publishing a requestable owner.");

        string call = "BootstrapStandingIdle(sim);";
        int caller = main.IndexOf(call, StringComparison.Ordinal);
        Assert(caller >= 0 && main.IndexOf(call, caller + call.Length, StringComparison.Ordinal) < 0,
            "The machine warmer remains the sole bootstrap caller.");
        string guard = main.Substring(Math.Max(0, caller - 400), Math.Min(400, caller));
        Assert(guard.Contains("StreamOwnsIdle(sim)", StringComparison.Ordinal)
            && guard.Contains("mKey.InstanceId == ResourceUtils.HashString64(\"idle\")", StringComparison.Ordinal),
            "Only the presenter's puppet idle path may bootstrap; server and other machines retain ordinary acquisition.");
    }

    [Test("ReplayDriverBootstrap.LateJoinResetBorrowsBeforeStreamSetup")]
    private static void ReplayDriverBootstrapLateJoinResetBorrowsBeforeStreamSetup()
    {
        uint postureDriver = 0; // Z invalidated the save-loaded driver.
        int detachedAcquires = 0;
        int postureAcquires = 0;
        Action? bootstrapTask = null;
        uint driver = ReplayDriverBootstrap.Resolve(postureDriver, true,
            () => { bootstrapTask = () => { postureAcquires++; postureDriver = 17; }; return 0; },
            () => { detachedAcquires++; return 23; }, out bool borrowed);

        Assert(borrowed && driver == 0 && postureAcquires == 0 && detachedAcquires == 0 && bootstrapTask != null,
            "The acquire must stay pending while a simulator task creates the posture owner, without a detached fallback.");
        bootstrapTask!();

        uint again = ReplayDriverBootstrap.Resolve(postureDriver, true,
            () => { postureAcquires++; return 29; },
            () => { detachedAcquires++; return 31; }, out borrowed);
        Assert(borrowed && again == postureDriver && postureAcquires == 1 && detachedAcquires == 0,
            "A subsequent history acquire must reuse the same owner.");
    }

    [Test("ReplayDriverBootstrap.FailedBootstrapDoesNotCreateSecondOwner")]
    private static void ReplayDriverBootstrapFailedBootstrapDoesNotCreateSecondOwner()
    {
        int detachedAcquires = 0;
        uint driver = ReplayDriverBootstrap.Resolve(0, true, () => 0,
            () => { detachedAcquires++; return 23; }, out bool borrowed);
        Assert(driver == 0 && borrowed && detachedAcquires == 0,
            "A missing resource stays pending; falling back to a detached driver recreates the ownership race.");
    }

    [Test("ReplayDriverBootstrap.OtherMachinesKeepDetachedAcquisition")]
    private static void ReplayDriverBootstrapOtherMachinesKeepDetachedAcquisition()
    {
        int bootstraps = 0;
        uint driver = ReplayDriverBootstrap.Resolve(0, false,
            () => { bootstraps++; return 17; }, () => 23, out bool borrowed);
        Assert(!borrowed && driver == 23 && bootstraps == 0,
            "Non-idle machines and non-presenter Sims retain the ordinary acquire path.");
        driver = ReplayDriverBootstrap.Resolve(19, false,
            () => { bootstraps++; return 17; }, () => 23, out borrowed);
        Assert(borrowed && driver == 19 && bootstraps == 0, "An existing own driver is always borrowed.");
    }

    [Test("ReplayDriverBootstrap.RepairWaitsOnlyForItsPendingIdle")]
    private static void ReplayDriverBootstrapRepairWaitsOnlyForItsPendingIdle()
    {
        Dictionary<uint, List<string>> pending = new()
        {
            { 5, new List<string> { "0|A|5|100|47570707|0|77|-2", "0|S|5|120|100" } }
        };
        Assert(ReplayDriverBootstrap.Pending(200, 77, pending, host => host + 100),
            "Repair must defer while this Sim's idle bootstrap and history setup are pending, using the object alias.");
        Assert(!ReplayDriverBootstrap.Pending(300, 77, pending, host => host + 100),
            "A different Sim's pending history must not suppress repair.");
        Assert(!ReplayDriverBootstrap.Pending(200, 88, pending, host => host + 100),
            "A different machine's pending history must not suppress idle repair.");
        pending.Clear();
        Assert(!ReplayDriverBootstrap.Pending(200, 77, pending, host => host + 100),
            "After history is retried or released there is no bootstrap ownership barrier.");
    }
}
