using SimsOnline.DedicatedServer.Authority;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// "reuse if unchanged" at a join checkpoint (JoinWorldReuse): reading the mod's fingerprint, the reuse
/// decision with its miss reasons, and the counters' line.
/// </summary>
internal static partial class Program
{
    private const string HomeScope = "h:1001|l:2001";

    // A mod reply as SimsOnlineServerHostMod/JoinFingerprint.cs writes one.
    private static string FingerprintReply(string scope = HomeScope, string objects = "54:6fba936a7d857f58",
        string inventories = "28:230e39fcf3806d36", string relationships = "3:f90efb809f3ca050", string ms = "2.33") =>
        $"scope={scope};objects={objects};inventories={inventories};relationships={relationships};ms={ms}";

    private static WorldFingerprint Fingerprint(string reply)
    {
        WorldFingerprint? parsed = WorldFingerprint.Parse(reply, out string? error);
        Assert(parsed != null && error == null, $"the reply should parse: {reply} ({error})");
        return parsed;
    }

    [Test("JoinWorldReuse.ParsesTheModsReplyAndRefusesTheRest")]
    private static void JoinWorldReuseParsesTheModsReplyAndRefusesTheRest()
    {
        WorldFingerprint fingerprint = Fingerprint(FingerprintReply());
        Assert(fingerprint.Scope == HomeScope, $"scope: {fingerprint.Scope}");
        Assert(fingerprint.Objects == new FingerprintPart(54, "6fba936a7d857f58"), $"objects: {fingerprint.Objects}");
        Assert(fingerprint.Inventories.Count == 28 && fingerprint.Relationships.Count == 3, "inventories and relationships");
        Assert(Math.Abs(fingerprint.ScanMs - 2.33) < 1e-9, $"ms: {fingerprint.ScanMs}");

        (string? reply, string expected)[] refused =
        {
            (null, "no fingerprint taken"),
            ("none", "no fingerprint taken"),
            ("error=NullReferenceException:Object reference not set", "NullReferenceException:Object reference not set"),
            // An older mod answers its usage text (an error reply, so the host throws) or its status line.
            ("status;replayDrivers=0;borrowedNow=0;on=0", "not a fingerprint"),
            ("scope=h:1|l:2;objects=54:aa;inventories=28:bb", "not a fingerprint"),
            ("scope=h:1|l:2;objects=x:aa;inventories=28:bb;relationships=3:cc", "not a fingerprint"),
        };
        foreach ((string? reply, string expected) in refused)
        {
            WorldFingerprint? parsed = WorldFingerprint.Parse(reply, out string? error);
            Assert(parsed == null && error != null && error.Contains(expected, StringComparison.Ordinal),
                $"'{reply}' should be refused with '{expected}', got {parsed}, error '{error}'");
        }
    }

    [Test("JoinWorldReuse.HitsOnlyWhenNothingChanged")]
    private static void JoinWorldReuseHitsOnlyWhenNothingChanged()
    {
        WorldFingerprint atSave = Fingerprint(FingerprintReply());

        JoinWorldReuse.Decision hit = JoinWorldReuse.Decide(atSave, Fingerprint(FingerprintReply(ms: "1.00")), null);
        Assert(hit.Reuse && hit.Misses.Count == 0, $"an equal fingerprint (scan time aside) is a hit: {hit}");

        JoinWorldReuse.Decision none = JoinWorldReuse.Decide(null, Fingerprint(FingerprintReply()), null);
        Assert(!none.Reuse && none.Misses.SequenceEqual(new[] { JoinWorldReuse.NoFingerprintYet }), $"no fingerprint at the save: {none}");

        JoinWorldReuse.Decision failed = JoinWorldReuse.Decide(atSave, null, "no answer in 3 s");
        Assert(!failed.Reuse && failed.Misses.SequenceEqual(new[] { JoinWorldReuse.Error }) && failed.Detail.Contains("no answer in 3 s", StringComparison.Ordinal),
            $"a scan that timed out: {failed}");

        JoinWorldReuse.Decision scope = JoinWorldReuse.Decide(atSave, Fingerprint(FingerprintReply(scope: "h:1001,1002|l:2001,2002")), null);
        Assert(!scope.Reuse && scope.Misses.SequenceEqual(new[] { JoinWorldReuse.Scope }), $"another household claimed: {scope}");

        // Nothing claimed at the save nor now: equal, but nothing was compared.
        WorldFingerprint empty = Fingerprint(FingerprintReply(scope: "-", objects: "0:cbf29ce484222325", inventories: "0:cbf29ce484222325",
            relationships: "0:cbf29ce484222325"));
        JoinWorldReuse.Decision nothing = JoinWorldReuse.Decide(empty, empty, null);
        Assert(!nothing.Reuse && nothing.Misses.SequenceEqual(new[] { JoinWorldReuse.Scope }), $"an empty scope is never a hit: {nothing}");
    }

    [Test("JoinWorldReuse.NamesEachPartThatChanged")]
    private static void JoinWorldReuseNamesEachPartThatChanged()
    {
        WorldFingerprint atSave = Fingerprint(FingerprintReply());

        (string reply, string[] reasons)[] cases =
        {
            (FingerprintReply(objects: "55:0000000000000001"), new[] { JoinWorldReuse.Objects }),
            (FingerprintReply(inventories: "28:0000000000000002"), new[] { JoinWorldReuse.Inventories }),
            (FingerprintReply(relationships: "3:0000000000000003"), new[] { JoinWorldReuse.Relationships }),
            (FingerprintReply(objects: "53:0000000000000004", inventories: "29:0000000000000005"), new[] { JoinWorldReuse.Objects, JoinWorldReuse.Inventories }),
        };
        foreach ((string reply, string[] reasons) in cases)
        {
            JoinWorldReuse.Decision decision = JoinWorldReuse.Decide(atSave, Fingerprint(reply), null);
            Assert(!decision.Reuse && decision.Misses.SequenceEqual(reasons), $"{reply}: expected {string.Join(",", reasons)}, got {decision}");
        }

        JoinWorldReuse.Decision moved = JoinWorldReuse.Decide(atSave, Fingerprint(FingerprintReply(inventories: "28:0000000000000002")), null);
        Assert(moved.Detail.Contains("inventories 28 -> 28 (same count, other entries)", StringComparison.Ordinal),
            $"a book read and put back elsewhere keeps the count: {moved.Detail}");
    }

    [Test("JoinWorldReuse.CountersAddUpAndPrintOneLine")]
    private static void JoinWorldReuseCountersAddUpAndPrintOneLine()
    {
        JoinReuseCounters counters = new();
        Assert(counters.Format() == "hits=0 misses=0 (no-fingerprint-yet=0 scope=0 objects=0 inventories=0 relationships=0 error=0) "
            + "scan-ms last=- max=- save-ms last=- max=- saves-skipped=0 seconds-saved=0.0", $"empty: {counters.Format()}");

        WorldFingerprint atSave = Fingerprint(FingerprintReply());
        counters.Record(JoinWorldReuse.Decide(null, null, null));
        counters.RecordSave(9876, joinCheckpoint: true);
        counters.RecordScan(3.3);
        counters.Record(JoinWorldReuse.Decide(atSave, Fingerprint(FingerprintReply()), null));
        counters.RecordScan(2.0);
        counters.Record(JoinWorldReuse.Decide(atSave, Fingerprint(FingerprintReply(objects: "53:01", inventories: "29:02")), null));
        counters.RecordSave(11539, joinCheckpoint: true);
        counters.RecordSave(4000, joinCheckpoint: false);
        counters.RecordScan(2.5);
        counters.Record(JoinWorldReuse.Decide(atSave, Fingerprint(FingerprintReply()), null));

        Assert(counters.Hits == 2 && counters.SavesSkipped == 2 && counters.Misses == 2, $"totals: {counters.Format()}");
        Assert(counters.MissesBy(JoinWorldReuse.Objects) == 1 && counters.MissesBy(JoinWorldReuse.Inventories) == 1
            && counters.MissesBy(JoinWorldReuse.NoFingerprintYet) == 1, "a miss counts under each of its reasons");
        // The first hit skipped a save like the 9876 ms one, the second one like the last save (a public one, 4000 ms).
        Assert(Math.Abs(counters.SecondsSaved - 13.876) < 1e-9, $"seconds saved: {counters.SecondsSaved}");
        Assert(counters.Format() == "hits=2 misses=2 (no-fingerprint-yet=1 scope=0 objects=1 inventories=1 relationships=0 error=0) "
            + "scan-ms last=2.5 max=3.3 save-ms last=11539 max=11539 saves-skipped=2 seconds-saved=13.9", $"line: {counters.Format()}");
    }
}
