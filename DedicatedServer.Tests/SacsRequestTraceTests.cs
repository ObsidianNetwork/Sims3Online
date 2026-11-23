using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("SacsRequestTrace.BoundsPagesAndRetainsFailures")]
    private static void SacsRequestTraceBoundsPagesAndRetainsFailures()
    {
        SacsRequestTrace trace = new SacsRequestTrace();
        Assert(!trace.Enabled, "Diagnostic is off by default.");
        trace.Add("native-rejection", -1);
        for (int i = 0; i < SacsRequestTrace.Capacity + 1; i++)
            trace.Add("ok-" + i, 0);
        string all = trace.Read(false, 0, 100);
        Assert(all.Contains("next=") && all.Contains("ok-256"), "Newest first, paged.");
        Assert(!all.Contains("native-rejection"), "General ring is bounded.");
        Assert(trace.Read(true, 0, 100).Contains("native-rejection"), "Success traffic cannot evict failures.");
        trace.Enabled = true;
        trace.Clear();
        Assert(trace.Enabled && trace.Count == 0 && trace.Failures == 0, "Clear keeps the switch and clears both rings.");
    }

    [Test("SacsRequestTrace.DistinguishesNoStateFromEmptyQueue")]
    private static void SacsRequestTraceDistinguishesNoStateFromEmptyQueue()
    {
        uint[] words = new uint[18];
        words[1] = 0x1234;
        string empty = SacsRequestTrace.Snapshot(words, 0, 0);
        Assert(empty.Contains("current:00000000/0,next:00000000/0"), "Native pointer absence is explicit.");
        Assert(empty.Contains("queued:0[]"), "Empty queue is explicit.");
        words[2] = 0x1000;
        words[3] = 2689084685;
        words[9] = 5;
        words[10] = 64;
        words[11] = 2083806798;
        string queued = SacsRequestTrace.Snapshot(words, 0, 0);
        Assert(queued.Contains("current:00001000/2689084685") && queued.Contains("queued:5[64/2083806798"),
            "Active state and queued flags/destination are retained.");
        Assert(queued.EndsWith(",...]"), "Bounded queue entries explicitly mark truncation.");
    }
}
