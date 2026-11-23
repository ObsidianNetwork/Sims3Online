using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("Card55.Contracts.ReceiptEpochBumpReusesCorrelationWithoutOldPlan")]
    private static void Card55ReceiptEpochBumpReusesCorrelationWithoutOldPlan()
    {
        var receipts = new ClientBoundaryRoute.ReceiptStore();
        string[] oldHeader = "0|Rt|1|9|1|0000000000000001|5".Split('|');
        string[] newHeader = "0|Rt|1|9|1|0000000000000002|7".Split('|');
        Assert(receipts.NavEpoch("0|Ne|1".Split('|')), "Trusted epoch.");
        Assert(receipts.Header(1, oldHeader), "First epoch header.");
        Assert(receipts.Point(1, "0|Vp|rt|1|9|0|0|100|0|100".Split('|')), "Old point.");
        Assert(receipts.Find(1, 1, 9)!.CompleteLeg(0), "Old execution observed.");
        Assert(!receipts.Header(1, newHeader) && receipts.Find(1, 1, 9) == null, "A duplicated identity is untrusted.");
        Assert(receipts.NavEpoch("0|Ne|2".Split('|')), "Authoritative epoch bump.");
        Assert(receipts.Find(1, 1, 9) == null && receipts.Report(1, true) == "rust-route-client=missing;epoch=2;fenced=0;open=0",
            "Drop old-epoch receipts before the replacement arrives.");
        Assert(!receipts.NavEpoch("0|Ne|1".Split('|')) && !receipts.Header(1, oldHeader), "Late old epoch fences.");
        Assert(!receipts.Point(1, "0|Vp|rt|1|9|0|0|100|0|100".Split('|')), "Reject stale epoch points.");
        Assert(receipts.NavEpoch("0|Ne|2".Split('|')), "Equal announcement unfences.");
        Assert(receipts.Header(2, newHeader), "Correlation one is reusable in the new epoch.");
        Assert(receipts.Point(2, "0|Vp|rt|1|9|0|0|300|0|100".Split('|')), "New point.");
        var current = receipts.Find(2, 1, 9)!;
        Assert(current.NavEpoch == 2 && current.Hash == 2 && current.ExecutedLegIndices().Length == 0,
            "The new plan cannot inherit old execution.");
        string page = receipts.ReportPlansPage(1, 9, 0, 16);
        Assert(page.Contains("Rt|1|9|1|0000000000000002|7") && page.Contains("|300|0|100") &&
            !page.Contains("0000000000000001") && !page.Contains("|100|0|100"), "Serve only the current plan.");
    }

    [Test("Card55.Contracts.SyncPageZeroRestartsPartialStage")]
    private static void Card55SyncPageZeroRestartsPartialStage()
    {
        var sync = new RustPrivateNavigation.SyncStore();
        sync.AcceptPage(1, 0, 1, "3:1:9:2");
        sync.AcceptPage(1, 0, 3, "4:2:10:3");
        sync.AcceptPage(1, 1, 3, "5:3:11:4");
        Assert(sync.AcceptPage(1, 0, 2, "6:4:12:5") ==
            "rust-navigation-sync=accepted;epoch=1;index=0;count=2;entries=1;active=1", "Restart page zero.");
        Assert(sync.SelectActor(9) != null && sync.KnownActors().Length == 1, "Partial replacement stays invisible.");
        sync.AcceptPage(1, 1, 2, "7:5:13:6");
        Assert(sync.SelectActor(9) == null && sync.SelectActor(12) != null && sync.SelectActor(13) != null,
            "Only the restarted sequence commits.");
        Assert(sync.KnownActors().Select(a => a.Wire).SequenceEqual(new ulong[] { 9, 12, 13 }),
            "Discarded staged rows cannot pollute the monotonic IdMap.");
    }

    [Test("Card55.Contracts.SyncAtomicReplacementAndIdUnion")]
    private static void Card55SyncAtomicReplacementAndIdUnion()
    {
        var sync = new RustPrivateNavigation.SyncStore();
        Assert(sync.AcceptPage(1, 0, 1, "3:-7:18446744073709551615:2") ==
            "rust-navigation-sync=accepted;epoch=1;index=0;count=1;entries=1;active=1", "Exact signed player/full actor ack.");
        Assert(sync.SelectActor(0).Wire == ulong.MaxValue && sync.SelectActor(0).Player == -7, "Default smallest kernel.");
        Assert(sync.AcceptPage(1, 0, 2, "4:1:9:3").EndsWith(";active=1"), "Partial page retains old ownership.");
        Assert(sync.SelectActor(ulong.MaxValue) != null, "Old owner remains until final page.");
        Assert(sync.AcceptPage(1, 1, 2, "5:2:10:4").EndsWith(";active=2"), "Final page swaps atomically.");
        Assert(sync.SelectActor(ulong.MaxValue) == null && sync.KnownActors().Length == 3, "Released claims gone; IdMap union retained.");
        Assert(sync.AcceptPage(1, 0, 1, "none") ==
            "rust-navigation-sync=accepted;epoch=1;index=0;count=1;entries=0;active=0", "Exact empty table.");
        Assert(sync.KnownActors().Length == 3 && sync.SelectActor(0) == null, "Empty ownership does not erase assignments.");
        Card55MustRefuse(() => sync.AcceptPage(1, 0, 1, "3:1:9:2"), "Conflicting assignment.");
        Card55MustRefuse(() => sync.AcceptPage(1, 1, 2, "3:1:20:5"), "Gap.");
        Card55MustRefuse(() => sync.AcceptPage(1, 0, 1, "0:1:20:5"), "Zero connection.");
        Card55MustRefuse(() => sync.AcceptPage(1, 0, 1, "3:1:20:1"), "Reserved kernel Sim.");
        Assert(sync.AcceptPage(1, 0, 1, "3:1:20:5").EndsWith(";active=1"), "Failure aborts staging.");
    }

    [Test("Card55.Contracts.ForcedEpochFenceResyncRestartsPlanCorrelation")]
    private static void Card55ForcedEpochFenceResyncRestartsPlanCorrelation()
    {
        var sync = new RustPrivateNavigation.SyncStore();
        sync.AcceptPage(1, 0, 1, "3:1:9:2");
        var fence = sync.BeginInvalidate();
        Assert(fence.Id == 1 && fence.Epoch == 1 && fence.Generation == 1, "Fresh fence.");
        Assert(sync.ApplyInvalidateResult(new() { Operation = 8, OriginalOperation = 4, Status = 1, Id = 1, Epoch = 1, Generation = 1 }) == null, "Done.");
        Assert(sync.NextInstallGeneration() == 2 && sync.NextPlanCorr() == 1 && sync.NextPlanCorr() == 2, "First install/corr.");
        sync.ResetAttachment();
        Card55MustRefuse(() => sync.AcceptPage(1, 0, 1, "3:1:9:2"), "Reconnect requires a forced epoch bump.");
        sync.AcceptPage(2, 0, 1, "3:1:9:2");
        fence = sync.BeginInvalidate();
        Assert(fence.Id == 1 && fence.Epoch == 2 && fence.Generation == 1, "New attachment IDs.");
        var retry = sync.ApplyInvalidateResult(new() { Operation = 8, OriginalOperation = 4, Status = 3, Id = 1, Epoch = 1, Generation = 2 });
        Assert(retry != null && retry.Id == 2 && retry.Epoch == 2 && retry.Generation == 2, "Exact stale resync example.");
        sync.ApplyInvalidateResult(new() { Operation = 8, OriginalOperation = 4, Status = 1, Id = 2, Epoch = 2, Generation = 2 });
        Assert(sync.NextInstallGeneration() == 3 && sync.NextFrameId() == 3, "Strictly increasing install and frame IDs.");
        Assert(sync.NextPlanCorr() == 1, "Bumped epoch permits first corr = 1.");
        Card55MustRefuse(() => sync.ApplyInvalidateResult(new() { Operation = 8, OriginalOperation = 4, Status = 1, Id = 1, Epoch = 1, Generation = 1 }), "Late fence cannot revive old epoch.");
    }

    private static ClientBoundaryRoute Card55FiveLegReceipt(ulong corr = 42, ulong actor = 18031287978496800304)
    {
        var route = new ClientBoundaryRoute();
        Assert(route.OnPlanHeader($"0|Rt|{corr}|{actor}|5|0123456789abcdef|3250".Split('|')), "Header.");
        uint[] types = [0, 0, 2147483648, 0, 0];
        int[] x = [100, 200, 210, 300, 300];
        for (int i = 0; i < 5; i++)
            Assert(route.OnPoint($"0|Vp|rt|{corr}|{actor}|{i}|{types[i]}|{x[i]}|0|100".Split('|')), "Point.");
        return route;
    }

    [Test("Card55.Contracts.DerivedKindFromCompletePointGrammar")]
    private static void Card55DerivedKindFromCompletePointGrammar()
    {
        var route = Card55FiveLegReceipt();
        Assert(Enumerable.Range(0, 5).Select(i => route.DerivedKind((uint)i)).SequenceEqual(new byte[] { 0, 1, 2, 3, 4 }),
            "SubPath Approach Through Exit SeatHandoff.");
        Assert(route.ExecutedLegIndices().Length == 0, "Derivation is never observation.");
        var malformed = new ClientBoundaryRoute();
        malformed.OnPlanHeader("0|Rt|7|9|3|0123456789abcdef|5".Split('|'));
        malformed.OnPoint("0|Vp|rt|7|9|0|2147483648|0|0|0".Split('|'));
        malformed.OnPoint("0|Vp|rt|7|9|1|0|0|0|0".Split('|'));
        malformed.OnPoint("0|Vp|rt|7|9|2|0|0|0|0".Split('|'));
        Card55MustRefuse(() => malformed.DerivedKind(0), "No approach before through.");
        malformed = new ClientBoundaryRoute();
        malformed.OnPlanHeader("0|Rt|7|9|2|0123456789abcdef|5".Split('|'));
        malformed.OnPoint("0|Vp|rt|7|9|0|1|0|0|0".Split('|'));
        malformed.OnPoint("0|Vp|rt|7|9|1|0|0|0|0".Split('|'));
        Card55MustRefuse(() => malformed.DerivedKind(0), "Ordinary private PathType must be zero.");
    }

    [Test("Card55.Contracts.PagingExactExampleAndAmbiguousActor")]
    private static void Card55PagingExactExampleAndAmbiguousActor()
    {
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(store.NavEpoch("0|Ne|1".Split('|')), "Epoch.");
        foreach (string frame in Card55FiveLegReceipt().Frames())
        {
            string[] fields = ("0|" + frame).Split('|');
            Assert(fields[1] == "Rt" ? store.Header(store.CurrentEpoch, fields) : store.Point(store.CurrentEpoch, fields), "Store frame.");
        }
        Assert(store.ReportPlansPage(42, 0, 0, 2) ==
            "rust-route-plans=page;epoch=1;corr=42;actor=18031287978496800304;cursor=0;count=2;total=6;next=2;more=1\n" +
            "Rt|42|18031287978496800304|5|0123456789abcdef|3250\n" +
            "Vp|rt|42|18031287978496800304|0|0|100|0|100", "Byte-for-byte page example.");
        Assert(store.ReportClientPage(42, 0, 0, 128) ==
            "rust-route-client=page;epoch=1;corr=42;actor=18031287978496800304;legs_total=5;received_legs=5;executed_count=0;cursor=0;count=0;next=0;more=0;WalkPhase=Ready;invalid_progressions=0;executed_slice=[]",
            "Exact received-only execution page.");
        Assert(store.ReportPlansPage(42, 0, 7, 2, 1) == "rust-route-client=blocked;reason=cursor-out-of-range", "Cursor bound.");
        Assert(store.Header(1, "0|Rt|42|9|1|0123456789abcdef|5".Split('|')), "Second actor.");
        Assert(store.ReportPlansPage(42, 0, 0, 2) == "rust-route-client=blocked;reason=ambiguous-actor", "No arbitrary actor choice.");
    }

    [Test("Card55.Contracts.MaximumEnvelopePagingIsLosslessAndBounded")]
    private static void Card55MaximumEnvelopePagingIsLosslessAndBounded()
    {
        var store = new ClientBoundaryRoute.ReceiptStore();
        Assert(store.NavEpoch("0|Ne|18446744073709551615".Split('|')), "Largest epoch header.");
        uint n = (uint)ClientBoundaryRoute.MaxLegs;
        Assert(n == 19782 && store.Header(store.CurrentEpoch, $"0|Rt|18446744073709551615|18446744073709551615|{n}|0123456789abcdef|18446744073709551615".Split('|')), "Max envelope.");
        for (uint i = 0; i < n; i++)
            Assert(store.Point(store.CurrentEpoch, $"0|Vp|rt|18446744073709551615|18446744073709551615|{i}|0|-2147483648|2147483647|-2147483648".Split('|')), "Point.");
        var route = store.Find(ulong.MaxValue, ulong.MaxValue)!;
        uint cursor = 0;
        List<string> frames = [];
        while (cursor < n + 1)
        {
            string page = store.ReportPlansPage(ulong.MaxValue, 0, cursor, uint.MaxValue, store.CurrentEpoch);
            Assert(page.Length <= 1900, "Carrier body bound.");
            string[] lines = page.Split('\n');
            Assert(lines.Length > 1 && lines.Length <= 17, "At least one frame per non-empty page, max 16.");
            frames.AddRange(lines.Skip(1));
            cursor += (uint)lines.Length - 1;
        }
        Assert(frames.SequenceEqual(route.Frames()), "No truncation, repeats or skipped frames.");
        for (uint i = 0; i < n; i++) Assert(route.CompleteLeg(i), "Synthetic observed completion.");
        cursor = 0;
        List<uint> indices = [];
        while (cursor < n)
        {
            string page = store.ReportClientPage(ulong.MaxValue, 0, cursor, uint.MaxValue, store.CurrentEpoch);
            Assert(page.Length <= 1900, "Execution page bound.");
            string slice = page[(page.IndexOf("executed_slice=[", StringComparison.Ordinal) + 16)..^1];
            uint[] batch = slice.Split(',').Select(uint.Parse).ToArray();
            Assert(batch.Length is > 0 and <= 128, "Bounded execution slice.");
            indices.AddRange(batch); cursor += (uint)batch.Length;
        }
        Assert(indices.SequenceEqual(route.ExecutedLegIndices()), "All execution indices survive paging.");
        Assert(store.Report(ulong.MaxValue, true) == "rust-route-client=blocked;reason=receipt-exceeds-carrier", "Unpaged contract unchanged.");
    }

    [Test("Card55.Contracts.MeasuredSnapshotCanonicalPlanesSlotsAndLocks")]
    private static void Card55MeasuredSnapshotCanonicalPlanesSlotsAndLocks()
    {
        var nav = new RustMeasuredSimNav(10);
        var a = nav.AddSurface(10, 1, 0, [[0, 0, 0], [100, 0, 0], [100, 0, 100], [0, 0, 100]]);
        var b = nav.AddSurface(20, 2, 0, [[100, 0, 0], [200, 0, 0], [200, 0, 100], [100, 0, 100]]);
        nav.AddPortal(a, b, 0, 0, 0, 0, [100, 0], [100, 100], 0, false, false);
        var door = nav.AddPortal(a, b, 0x80000000, ulong.MaxValue, 4, 5, [100, 0], [100, 100], 0, true, true);
        var bindings = nav.Bind(7, 2, [new() { Wire = ulong.MaxValue, Kernel = 2 }]);
        Assert(bindings.Provenance == 0 && bindings.Portals[door].EntrySlot == 4 &&
            bindings.Portals[door].ExitSlot == 5 && bindings.Keys.SequenceEqual(new ushort[] { 2 }), "Canonical slots and allowed locked key.");
        byte[] bytes = nav.Encode();
        Assert(System.Text.Encoding.ASCII.GetString(bytes, 0, 8) == "SIMNAV01" && BitConverter.ToUInt32(bytes, 12) == 6, "Measured multi-lot header.");
        Assert(BitConverter.ToUInt32(bytes, 36) == 2 && BitConverter.ToUInt32(bytes, 40) == 2, "Dense counts.");
        ulong expected = 0xcbf29ce484222325;
        for (int i = 0; i < bytes.Length; i++) expected = unchecked((expected ^ (i is >= 56 and < 64 ? (byte)0 : bytes[i])) * 0x100000001b3);
        Assert(BitConverter.ToUInt64(bytes, 56) == expected, "SIMNAV header-zeroed FNV parity.");
        Assert(a.Center.SequenceEqual(new int[] { 50, 0, 50 }) && a.Vertices[0].SequenceEqual(new int[] { 0, 0, 0 }), "Exact centroid and canonical first vertex.");
        Assert(RustMeasuredSimNav.SlotZeroIsEntry([0, 0, 0], [100, 0, 0], a.Center), "Geometric orientation ignores actor position.");
        Assert(RustMeasuredSimNav.SlotZeroIsEntry([0, 0, 0], [50, 0, 50], a), "Containment in canonical cell_a takes precedence.");
        Card55MustRefuse(() => nav.AddSurface(10, 3, 0, [[0, 0, 0], [0, 0, 100], [100, 1, 100], [100, 0, 0]]), "Nonplanar surface.");
        Card55MustRefuse(() => nav.AddSurface(10, 3, 0, [[0, 0, 0], [100, 0, 0], [50, 0, 20], [100, 0, 100], [0, 0, 100]]), "Concave polygon.");
        Card55MustRefuse(() => nav.AddPortal(a, b, 0, 0, 0, 0, [50, 0], [50, 100], 0, false, false), "Cross-lot jambs must meet both cells.");
    }

    private sealed class Card55RouteSink : RustPrivateNavigation.IRouteBuilder
    {
        public List<string> Calls = [];
        public bool SubPath(RustPrivateNavigation.Leg leg) { Calls.Add("sub:" + leg.Index); return true; }
        public bool Portal(RustPrivateNavigation.Leg approach, RustPrivateNavigation.Leg through, RustPrivateNavigation.Leg exit)
        { Calls.Add($"portal:{through.Object}:{through.EntrySlot}:{through.ExitSlot}"); return true; }
    }

    [Test("Card55.Contracts.TypedExecutorDispatchesAuthoritativePortalTriple")]
    private static void Card55TypedExecutorDispatchesAuthoritativePortalTriple()
    {
        var envelope = new RustPrivateNavigation.Envelope { Legs = Enumerable.Range(0, 5).Select(i =>
            new RustPrivateNavigation.Leg { Index = (uint)i, Kind = (byte)i }).ToArray() };
        envelope.Legs[2].PathType = 0x80000001;
        for (int i = 1; i <= 3; i++)
        { envelope.Legs[i].Object = ulong.MaxValue; envelope.Legs[i].EntrySlot = 4; envelope.Legs[i].ExitSlot = 5; }
        var sink = new Card55RouteSink();
        Assert(RustPrivateNavigation.BuildTypedRoute(envelope, sink) &&
            sink.Calls.SequenceEqual(new[] { "sub:0", "portal:18446744073709551615:4:5" }),
            "No planner or cross-floor polyline substitutes for the typed stair portal.");
        envelope.Legs[3].ExitSlot = 6;
        sink.Calls.Clear();
        Card55MustRefuse(() => RustPrivateNavigation.BuildTypedRoute(envelope, sink), "Portal triple binding mismatch.");
        Assert(sink.Calls.Count == 0, "Validate every leg before modifying the route.");
    }
}
