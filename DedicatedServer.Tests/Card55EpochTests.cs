using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    private static string[] Card55EpochHeader(string corr = "1", string actor = "9", string hash = "0000000000000001")
        => $"0|Rt|{corr}|{actor}|2|{hash}|5".Split('|');

    private static string[] Card55EpochPoint(int index, string corr = "1", string actor = "9")
        => $"0|Vp|rt|{corr}|{actor}|{index}|0|100|0|100".Split('|');

    [Test("Card55.Epoch.PlanBeforeAnnouncementIsRefused")]
    private static void Card55PlanBeforeAnnouncementIsRefused()
    {
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(!store.Header(0, Card55EpochHeader()), "A plan without a trusted epoch is refused.");
        Assert(!store.Point(0, Card55EpochPoint(0)), "A point without a trusted epoch is refused.");
        Assert(store.Find(0, 1, 9) == null, "Refusal cannot leave a partial receipt.");
    }

    [Test("Card55.Epoch.DuplicateHeaderRemovesReceiptAndClosesGroup")]
    private static void Card55DuplicateHeaderRemovesReceiptAndClosesGroup()
    {
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(store.NavEpoch("0|Ne|1".Split('|')), "Epoch.");
        Assert(store.Header(1, Card55EpochHeader()) && store.Point(1, Card55EpochPoint(0)), "Partial receipt.");
        Assert(!store.Header(1, Card55EpochHeader()), "Duplicate is refused.");
        Assert(store.Find(1, 1, 9) == null && !store.Point(1, Card55EpochPoint(1)),
            "Neither version of a duplicated identity can accept points.");
    }

    [Test("Card55.Epoch.MalformedHeaderClosesPartialGroup")]
    private static void Card55MalformedHeaderClosesPartialGroup()
    {
        foreach (bool parseable in new[] { false, true })
        {
            var store = new ClientBoundaryRoute.ReceiptStore();
            Assert(store.NavEpoch("0|Ne|2".Split('|')), "Epoch.");
            Assert(store.Header(2, Card55EpochHeader()) && store.Point(2, Card55EpochPoint(0)), "Partial receipt.");
            Assert(!store.Header(2, Card55EpochHeader(parseable ? "1" : "x", hash: "bad")), "Malformed header.");
            Assert(!store.Point(2, Card55EpochPoint(1)), "The malformed header closes the group even without a key.");
            var retained = store.Find(2, 1, 9);
            Assert(parseable ? retained == null : retained != null && retained.Frames().Length == 2,
                "Only a parseable duplicate identity is removed.");
        }
    }

    [Test("Card55.Epoch.WrongPointClosesGroup")]
    private static void Card55WrongPointClosesGroup()
    {
        foreach (string[] wrong in new[] { Card55EpochPoint(0, "2"), Card55EpochPoint(0, actor: "10"), Card55EpochPoint(1) })
        {
            var store = new ClientBoundaryRoute.ReceiptStore();
            Assert(store.NavEpoch("0|Ne|1".Split('|')) && store.Header(1, Card55EpochHeader()), "Open group.");
            Assert(!store.Point(1, wrong), "Wrong key or index is refused.");
            Assert(!store.Point(1, Card55EpochPoint(0)) && store.Find(1, 1, 9)!.Frames().Length == 1,
                "A later matching point cannot reopen the receipt.");
        }
    }

    [Test("Card55.Epoch.AnnouncementsFenceAndCloseWithoutErasingOnMalformed")]
    private static void Card55AnnouncementsFenceAndCloseWithoutErasingOnMalformed()
    {
        foreach (string bad in new[] { "0|Ne|0", "0|Ne|02", "0|Ne|+2", "0|Ne|-2", "0|Ne| 2", "0|Ne|2 ",
            "0|Ne|x", "0|Ne|18446744073709551616", "0|Ne", "0|Ne|2|1", "1|Ne|2", "0|Ne|1" })
        {
            var store = new ClientBoundaryRoute.ReceiptStore();
            Assert(store.NavEpoch("0|Ne|2".Split('|')) && store.Header(2, Card55EpochHeader()) &&
                store.Point(2, Card55EpochPoint(0)), "Adopt and start a partial group.");
            Assert(!store.NavEpoch(bad.Split('|')) && store.Fenced && store.Open == null && store.CurrentEpoch == 2,
                "Malformed or older announcement fences and closes: " + bad);
            Assert(store.Find(2, 1, 9)!.Frames().Length == 2, "Malformed Ne does not clear receipts.");
            Assert(!store.Header(2, Card55EpochHeader("2")) && !store.Point(2, Card55EpochPoint(1)), "Fenced records refuse.");
            Assert(store.NavEpoch("0|Ne|2".Split('|')) && !store.Fenced && store.Open == null &&
                store.Find(2, 1, 9)!.Frames().Length == 2, "Equal Ne is idempotent and unfences without reopening.");
            Assert(!store.Point(2, Card55EpochPoint(1)), "An equal Ne never resumes the old group.");
            Assert(store.NavEpoch("0|Ne|3".Split('|')) && store.CurrentEpoch == 3 && store.Find(2, 1, 9) == null,
                "A higher Ne drops all receipts.");
        }
    }

    [Test("Card55.Epoch.ReadyClosesGroupAndNewHeaderClosesPrevious")]
    private static void Card55ReadyClosesGroupAndNewHeaderClosesPrevious()
    {
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(store.NavEpoch("0|Ne|1".Split('|')) && store.Header(1, Card55EpochHeader()), "First group.");
        Assert(store.Point(1, Card55EpochPoint(0)) && store.Point(1, Card55EpochPoint(1)), "Complete group.");
        Assert(store.Open == null && !store.Point(1, Card55EpochPoint(1)), "Ready is closed.");
        Assert(store.Header(1, Card55EpochHeader("2")) && store.Header(1, Card55EpochHeader("3")), "Only one open group.");
        Assert(!store.Point(1, Card55EpochPoint(0, "2")) && !store.Point(1, Card55EpochPoint(0, "3")), "Wrong key closes new group too.");
        Assert(store.Find(1, 2, 9)!.Frames().Length == 1 && store.Find(1, 3, 9)!.Frames().Length == 1, "Neither gains points.");
    }

    [Test("Card55.Epoch.PagingPinsEpochBeforeSelectingReceipt")]
    private static void Card55PagingPinsEpochBeforeSelectingReceipt()
    {
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(store.NavEpoch("0|Ne|2".Split('|')) && store.Header(2, Card55EpochHeader()) &&
            store.Point(2, Card55EpochPoint(0)) && store.Point(2, Card55EpochPoint(1)), "Receipt.");
        Assert(store.ReportPlansPage(1, 9, 0, 2).StartsWith("rust-route-plans=page;epoch=2;corr=1;"), "Epoch is first page key.");
        Assert(store.ReportClientPage(1, 9, 0, 2).StartsWith("rust-route-client=page;epoch=2;corr=1;"), "Execution epoch.");
        Assert(store.ReportPlansPage(1, 9, 2, 16) == "rust-route-client=blocked;reason=epoch-required" &&
            store.ReportClientPage(1, 9, 1, 16) == "rust-route-client=blocked;reason=epoch-required", "No continuation without an epoch.");
        Assert(store.NavEpoch("0|Ne|3".Split('|')), "Bump between pages, without a replacement.");
        Assert(store.ReportPlansPage(1, 9, 2, 16, 2) == "rust-route-client=blocked;reason=epoch-mismatch" &&
            store.ReportClientPage(1, 9, 0, 16, 2) == "rust-route-client=blocked;reason=epoch-mismatch", "Epoch check precedes missing-receipt lookup.");
        Assert(store.Header(3, Card55EpochHeader()), "Reused correlation.");
        Assert(store.ReportPlansPage(1, 9, 0, 16, 2) == "rust-route-client=blocked;reason=epoch-mismatch", "Page zero can also be pinned.");
        Assert(store.Report(1, false).Contains(";epoch=3;fenced=0;open=1"), "Collector can see attribution state.");
    }

    [Test("Card55.Epoch.PurgeBeforePageZeroAckLeavesNoPlanInAnyGather")]
    private static void Card55PurgeBeforePageZeroAckLeavesNoPlanInAnyGather()
    {
        string header = string.Join('|', Card55EpochHeader());
        string point = string.Join('|', Card55EpochPoint(0));
        string native = "0|Rt|9|1|2|3|0|0|0";
        string[] ring = [native, header, point];
        List<string> head = ["A|7|9|1|0|99|-2", header[2..]];
        List<string> tail = [point[2..]];
        int purges = 0;
        var sync = new RustPrivateNavigation.SyncStore(() =>
        {
            ClientBoundaryRoute.DiscardPlanRecords(ring, true);
            ClientBoundaryRoute.DiscardPlanRecords(head, false);
            ClientBoundaryRoute.DiscardPlanRecords(tail, false);
            purges++;
        });
        string ack = sync.AcceptPage(2, 0, 2, "3:1:9:2");
        Assert(ack.StartsWith("rust-navigation-sync=accepted;epoch=2;index=0;count=2;") && purges == 1,
            "Purge runs before acknowledging page zero, rather than waiting for the final page.");
        Assert(ring[0] == native && !ring.Any(line => ClientBoundaryRoute.IsPlanRecord(line, true)), "Ring page has only native records.");
        var snapshot = DriverIntroduction.Head(head[0], null, [], []);
        var snapshotDriver = DriverIntroduction.Snapshot(head, null, 0, null!, true, [], [], tail);
        Assert(!snapshot.Any(line => ClientBoundaryRoute.IsPlanRecord(line, true)) &&
            !snapshotDriver.Any(line => ClientBoundaryRoute.IsPlanRecord(line, false)), "Both join-history gathers are free of private plans.");
        sync.AcceptPage(2, 1, 2, "4:2:10:3");
        Assert(purges == 1, "The final page does not repeat the purge.");
    }

    [Test("Card55.Epoch.RecorderHistoryNeverGathersPrivatePlanCopies")]
    private static void Card55RecorderHistoryNeverGathersPrivatePlanCopies()
    {
        string header = string.Join('|', Card55EpochHeader())[2..];
        string point = string.Join('|', Card55EpochPoint(0))[2..];
        var snapshot = DriverIntroduction.Head("A|7|9|1|0|99|-2", header, [point], ["0|" + header]);
        var driver = DriverIntroduction.Snapshot(["A|7|9|1|0|99|-2", header], point, 0, null!, true, [point], [header], [point]);
        Assert(!snapshot.Any(line => ClientBoundaryRoute.IsPlanRecord(line, true)) &&
            !driver.Any(line => ClientBoundaryRoute.IsPlanRecord(line, false)), "The actual pure history assemblers never replay private plan records.");
    }

    [Test("Card55.Epoch.FreshJoinResetAndSynchronousDispatchWiring")]
    private static void Card55FreshJoinResetAndSynchronousDispatchWiring()
    {
        string root = SolutionRoot();
        string shell = File.ReadAllText(Path.Combine(root, "SimsOnlineServerHostMod", "ServerHostMain.cs"));
        string client = File.ReadAllText(Path.Combine(root, "SimsOnline.Client", "Program.cs"));
        string module = File.ReadAllText(Path.Combine(root, "SimsOnlineServerHostMod", "Presentation", "Module.cs"));
        string connect = shell[shell.IndexOf("private static void HandleConnect(", StringComparison.Ordinal)..];
        connect = connect[..connect.IndexOf("// on a warm v11", StringComparison.Ordinal)];
        Assert(!connect.Contains("Receipts.Clear()") && connect.Contains("sCurrentRole != \"client\""),
            "A bridge reconnect preserves client receipts and presentation.");
        int join = client.IndexOf("await WriteCommandAsync(new ClientCommand.JoinSession", StringComparison.Ordinal);
        int reset = client.LastIndexOf("\"presentation-reset\"", join, StringComparison.Ordinal);
        Assert(reset >= 0 && join - reset < 2000, "The fresh join path resets presentation before joining.");
        Assert(module.Contains("Register(\"Ne\", new PresentationLineHandler(Walks.ReplayNavEpoch))") &&
            module.Contains("ClientBoundaryRoute.Receipts.Clear()"), "The registered reset clears navigation state.");
        string dispatch = shell[shell.IndexOf("private static string ApplyReplayBatch(", StringComparison.Ordinal)..];
        Assert(dispatch.IndexOf("moduleHandler(f)", StringComparison.Ordinal) < dispatch.IndexOf("HoldForRoute(", StringComparison.Ordinal) &&
            dispatch.Contains("kind == \"Ne\" || kind == \"Rt\" || kind == \"Vp\""), "Navigation is consumed before the walk hold path, including without a module.");
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(store.NavEpoch("0|Ne|9".Split('|')) && store.Header(9, Card55EpochHeader()) &&
            store.Point(9, Card55EpochPoint(0)), "Partial stream before resume.");
        Assert(store.Point(9, Card55EpochPoint(1)), "Resume retains the open group.");
        store.Clear();
        Assert(store.CurrentEpoch == 0 && !store.Fenced && store.Open == null && store.Find(9, 1, 9) == null &&
            store.NavEpoch("0|Ne|1".Split('|')), "A fresh join can adopt a restarted server epoch.");
    }
}
