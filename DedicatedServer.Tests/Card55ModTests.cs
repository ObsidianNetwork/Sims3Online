using System.Text.Json;
using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    private static JsonDocument Card55Vectors() =>
        JsonDocument.Parse(File.ReadAllText("testdata/card55-private-attachment-v1.json"));

    [Test("Card55.CodecMatchesEveryAuthoredVector")]
    private static void Card55CodecMatchesEveryAuthoredVector()
    {
        using var fixture = Card55Vectors();
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            byte[] bytes = Convert.FromHexString(vector.GetProperty("wire_hex").GetString()!);
            var frame = RustPrivateNavigation.Decode(bytes);
            Assert(bytes.SequenceEqual(RustPrivateNavigation.Encode(frame)),
                "Byte parity for " + vector.GetProperty("name").GetString());
        }
        byte[] envelopeBytes = Convert.FromHexString(fixture.RootElement.GetProperty("envelope_hex").GetString()!);
        var envelope = RustPrivateNavigation.DecodeEnvelope(envelopeBytes);
        Assert(envelope.Actor == 0xfedcba9876543210UL && envelope.Correlation == envelope.Actor, "Full u64 identities.");
        Assert(envelope.Legs[0].PathType == 0x8a863e32U && envelope.Legs[0].EntrySlot == 4 &&
            envelope.Legs[0].ExitSlot == 5, "Unsigned type and authoritative slots.");
        Assert(envelopeBytes.SequenceEqual(RustPrivateNavigation.EncodeEnvelope(envelope)), "Envelope byte parity.");
    }

    private static void Card55MustRefuse(Action action, string reason)
    {
        try { action(); }
        catch (FormatException) { return; }
        throw new InvalidOperationException("Expected format refusal: " + reason);
    }

    [Test("Card55.CodecRejectsTruncationTrailingAndInvalidFields")]
    private static void Card55CodecRejectsTruncationTrailingAndInvalidFields()
    {
        using var fixture = Card55Vectors();
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            byte[] bytes = Convert.FromHexString(vector.GetProperty("wire_hex").GetString()!);
            for (int n = 0; n < bytes.Length; n++)
                Card55MustRefuse(() => RustPrivateNavigation.Decode(bytes[..n]), "truncated record");
            Card55MustRefuse(() => RustPrivateNavigation.Decode([.. bytes, 0]), "trailing record byte");
            byte[] bad = (byte[])bytes.Clone();
            bad[7] = 1;
            Card55MustRefuse(() => RustPrivateNavigation.Decode(bad), "reserved byte");
            bad[7] = 0; bad[4] = 2;
            Card55MustRefuse(() => RustPrivateNavigation.Decode(bad), "version");
        }
        byte[] envelope = Convert.FromHexString(fixture.RootElement.GetProperty("envelope_hex").GetString()!);
        for (int n = 0; n < envelope.Length; n++)
            Card55MustRefuse(() => RustPrivateNavigation.DecodeEnvelope(envelope[..n]), "truncated envelope");
        Card55MustRefuse(() => RustPrivateNavigation.DecodeEnvelope([.. envelope, 0]), "trailing envelope");
        envelope[60] = 255;
        Card55MustRefuse(() => RustPrivateNavigation.DecodeEnvelope(envelope), "count checked before allocation");
    }

    [Test("Card55.ExactAcknowledgementBodies")]
    private static void Card55ExactAcknowledgementBodies()
    {
        using var fixture = Card55Vectors();
        byte[] bytes = Convert.FromHexString(fixture.RootElement.GetProperty("vectors")[7].GetProperty("wire_hex").GetString()!);
        Assert(RustPrivateNavigation.ResultAcknowledgement(bytes) ==
            "rust-navigation-result=accepted;operation=8;request=4;frame=" + Convert.ToHexString(bytes), "Exact result body.");
        string blob = RustPrivateNavigation.BlobName(7, 2, ulong.MaxValue, ulong.MaxValue, 4);
        Assert(blob == "nav-0000000000000007-0000000000000002-ffffffffffffffff-ffffffffffffffff-0000000000000004", "Canonical blob.");
        Assert(RustPrivateNavigation.OfferAcknowledgement(blob, 4, 7, 2, ulong.MaxValue, ulong.MaxValue) ==
            "rust-navigation-offer=accepted;blob=" + blob + ";request=4;epoch=7;generation=2;corr=18446744073709551615;actor=18446744073709551615",
            "Exact offer body.");
    }

    [Test("Card55.NavigationBlobsAreBoundedOrderedAndIsolated")]
    private static void Card55NavigationBlobsAreBoundedOrderedAndIsolated()
    {
        var blobs = new RustPrivateNavigation.BlobStore();
        byte[]? result;
        Assert(blobs.Add("nav-a", 0, 2, "01", out result) && result == null, "First chunk.");
        Assert(blobs.Add("nav-b", 0, 1, "FE", out result) && result!.SequenceEqual(new byte[] { 254 }), "Independent upload.");
        Assert(!blobs.Add("nav-a", 0, 2, "02", out result), "Duplicate first chunk refuses.");
        Assert(!blobs.Add("nav-a", 1, 3, "02", out result), "Count cannot change.");
        Assert(blobs.Add("nav-a", 1, 2, "02", out result) && result!.SequenceEqual(new byte[] { 1, 2 }), "Original bytes preserved.");
        Assert(!blobs.Add("probe", 0, 1, "00", out result), "Ordinary diagnostic blobs stay outside this store.");
        Assert(!blobs.Add("nav-odd", 0, 1, "0", out result), "Whole hex bytes only.");
        Assert(!blobs.Add("nav-invalid", 0, 1, "GG", out result), "Hex checked.");
        for (int i = 0; i < 14; i++)
            Assert(blobs.Add("nav-" + i, 0, 1, "00", out result), "Bounded entry " + i);
        Assert(!blobs.Add("nav-full", 0, 1, "00", out result), "Sixteen entries maximum including completed uploads.");
        Assert(blobs.Take("nav-a")!.SequenceEqual(new byte[] { 1, 2 }) && blobs.Take("nav-a") == null, "Consume once.");
    }

    [Test("Card55.ClientGrammarPreservesUnsignedIdentityAndRejectsCollision")]
    private static void Card55ClientGrammarPreservesUnsignedIdentityAndRejectsCollision()
    {
        var route = new ClientBoundaryRoute();
        string header = "0|Rt|18446744073709551615|18364758544493064720|1|0123456789abcdef|1500";
        Assert(route.OnPlanHeader(header.Split('|')), "Private header grammar.");
        Assert(route.Correlation == ulong.MaxValue && route.Actor == 0xfedcba9876543210UL, "No width loss.");
        Assert(route.OnPoint("0|Vp|rt|18446744073709551615|18364758544493064720|0|2147483649|-2|300|-50".Split('|')), "Unsigned high bit.");
        Assert(route.Frames()[0] == header[2..] && route.Frames().Length == 2, "Verbatim frames without sequence.");
        Assert(route.ExecutedLegIndices().Length == 0, "Packet arrival is never execution.");
        Assert(!new ClientBoundaryRoute().OnPlanHeader("0|Rt|7|1|2|0123456789abcdeg|5".Split('|')), "Strict hex.");
        Assert(!new ClientBoundaryRoute().OnPlanHeader("0|Rt|+7|1|2|0123456789abcdef|5".Split('|')), "Unsigned grammar.");
        Assert(ClientBoundaryRoute.IsNativeRoute("0|Rt|42|1.0|2|3|0|0|0".Split('|')), "Legacy route.");
        Assert(!ClientBoundaryRoute.IsNativeRoute(header.Split('|')), "Private header never native.");
        var malformed = new ClientBoundaryRoute();
        Assert(malformed.OnPlanHeader("0|Rt|7|9|1|0123456789abcdef|5".Split('|')), "Header.");
        Assert(!malformed.OnPoint("0|Vp|rt|7|9|0|-2147483647|0|0|0".Split('|')), "Signed PathType refuses.");
        Assert(!malformed.OnPoint("0|Vp|rt|7|9|0|4294967296|0|0|0".Split('|')), "Overflow refuses.");
        Assert(!malformed.OnPoint("0|Vp|rt|8|9|0|1|0|0|0".Split('|')), "Correlation bound.");
    }

    [Test("Card55.ClientExecutionRequiresOrderedObservedLegsAndSettle")]
    private static void Card55ClientExecutionRequiresOrderedObservedLegsAndSettle()
    {
        var route = new ClientBoundaryRoute();
        Assert(route.OnPlanHeader("0|Rt|7|9|2|0123456789abcdef|5".Split('|')), "Header.");
        Assert(route.OnPoint("0|Vp|rt|7|9|0|2147483648|0|0|0".Split('|')), "Approach target.");
        Assert(route.OnPoint("0|Vp|rt|7|9|1|2147483649|10|0|0".Split('|')), "Portal target.");
        Assert(!route.CompleteLeg(1), "Skipping refused.");
        Assert(route.CompleteLeg(0) && !route.CompleteLeg(0), "Observed leg exactly once.");
        Assert(route.CompleteLeg(1), "Next observed leg.");
        Assert(!route.Settle(false, 101) && route.Phase != "Halted", "Over 100 mm is not settled.");
        Assert(route.Settle(false, 100) && route.Phase == "Halted", "Exact threshold.");
        Assert(route.ExecutedLegIndices().SequenceEqual(new uint[] { 0, 1 }), "Actual ordered completion only.");
        Assert(route.InvalidProgressions == 2, "Skip and replay remain visible.");
    }

    [Test("Card55.ClientReceiptStoreScopesBoundsAndClears")]
    private static void Card55ClientReceiptStoreScopesBoundsAndClears()
    {
        var receipts = new ClientBoundaryRoute.ReceiptStore();
        Assert(receipts.NavEpoch("0|Ne|1".Split('|')), "Epoch.");
        Assert(!receipts.Point(1, "0|Vp|rt|7|9|0|0|0|0|0".Split('|')), "No header, no receipt.");
        Assert(receipts.Header(1, "0|Rt|7|9|1|0123456789abcdef|5".Split('|')), "First actor.");
        Assert(receipts.Header(1, "0|Rt|7|10|1|0123456789abcdef|5".Split('|')), "Actor is part of key.");
        Assert(receipts.Point(1, "0|Vp|rt|7|10|0|0|0|0|0".Split('|')), "Only the named open actor.");
        Assert(receipts.Find(7, 9)!.Frames().Length == 1 && receipts.Find(7, 10)!.Frames().Length == 2, "No cross-talk.");
        Assert(!receipts.Header(1, "0|Rt|7|9|1|0123456789abcdef|5".Split('|')) && receipts.Find(7, 9) == null,
            "Duplicate identity is removed without removing another actor.");
        for (ulong i = 8; i < 24; i++)
            Assert(receipts.Header(1, $"0|Rt|{i}|9|1|0123456789abcdef|5".Split('|')), "Bounded insertion.");
        Assert(receipts.Find(7, 10) == null && receipts.Find(8, 9) != null, "Oldest of seventeen evicted after removal.");
        receipts.Clear();
        Assert(receipts.Find(7, 10) == null && receipts.Report(7, true) == "rust-route-client=missing;epoch=0;fenced=0;open=0", "World/session reset clears.");

        Assert(receipts.NavEpoch("0|Ne|1".Split('|')), "New session announcement.");
        Assert(receipts.Header(1, "0|Rt|99|9|40|0123456789abcdef|5".Split('|')), "Large receipt.");
        for (int i = 0; i < 40; i++)
            Assert(receipts.Point(1, $"0|Vp|rt|99|9|{i}|2147483648|1000000|1000000|1000000".Split('|')), "Ordered point.");
        Assert(receipts.Report(99, true) == "rust-route-client=blocked;reason=receipt-exceeds-carrier",
            "Never truncate or overflow the bridge carrier.");
        Assert(receipts.Find(99, 9)!.Frames().Length == 41, "Full receipt remains in memory.");
    }
}
