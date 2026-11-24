using SimsOnlineServerHost;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// LineDelayQueue: the order and due times of a client's stream lines while they wait the line delay (LineDelay.cs in
/// the mod). Batches leave in the order they arrived, each its delay after it arrived, and a Sim whose walk line
/// waits counts as about to walk.
/// </summary>
internal static partial class Program
{
    [Test("LineDelayQueue.ABatchIsDueItsDelayAfterItArrived")]
    private static void LineDelayQueueABatchIsDueItsDelayAfterItArrived()
    {
        LineDelayQueue queue = new LineDelayQueue();
        queue.Add(new[] { "1|T|5|6" }, 1000, 120.0);
        Assert(queue.Waiting == 1, "The batch waits.");
        Assert(queue.Next(1119) == null, "Not before its delay has passed.");
        LineDelayQueue.Batch batch = queue.Next(1120);
        Assert(batch != null && batch.Lines[0] == "1|T|5|6" && batch.ArrivedMs == 1000, "Due at arrival plus the delay, with its arrival time.");
        Assert(queue.Waiting == 0 && queue.Next(5000) == null, "Nothing is left.");
    }

    [Test("LineDelayQueue.ANewerBatchNeverPassesAnOlderOne")]
    private static void LineDelayQueueANewerBatchNeverPassesAnOlderOne()
    {
        LineDelayQueue queue = new LineDelayQueue();
        queue.Add(new[] { "1|U|5" }, 1000, 120.0);
        queue.Add(new[] { "2|Ob|5|SetPosition:v|1~2~3" }, 1010, 0.0); // the delay was turned off meanwhile
        Assert(queue.Next(1050) == null, "The newer batch waits behind the older one.");
        LineDelayQueue.Batch first = queue.Next(1120);
        LineDelayQueue.Batch second = queue.Next(1120);
        Assert(first != null && first.Lines[0] == "1|U|5", "The older batch first.");
        Assert(second != null && second.Lines[0].StartsWith("2|Ob|"), "Then the newer one, due no earlier than the older.");
    }

    [Test("LineDelayQueue.AWaitingWalkLineMarksItsSim")]
    private static void LineDelayQueueAWaitingWalkLineMarksItsSim()
    {
        LineDelayQueue queue = new LineDelayQueue();
        queue.Add(new[] { "1|Rs|77|1|2|3", "2|Pz|77|1|2|3|0|0|1" }, 1000, 120.0);
        queue.Add(new[] { "3|Rs|77|4|5|6" }, 1040, 120.0);
        Assert(queue.WalkPending("77") && !queue.WalkPending("78"), "Only the Sim with a walk line.");
        queue.Next(1120);
        Assert(queue.WalkPending("77"), "A second walk line of the same Sim still waits.");
        queue.Next(1160);
        Assert(!queue.WalkPending("77"), "No walk line waits once both batches left.");
    }

    [Test("LineDelayQueue.FieldReadsWithoutSplitting")]
    private static void LineDelayQueueFieldReadsWithoutSplitting()
    {
        Assert(LineDelayQueue.Field("9|Ob|42|SetTransform:m|1~0~0", 1) == "Ob", "The kind.");
        Assert(LineDelayQueue.Field("9|Ob|42|SetTransform:m|1~0~0", 3) == "SetTransform:m", "A middle field.");
        Assert(LineDelayQueue.Field("9|Ob|42|SetTransform:m|1~0~0", 4) == "1~0~0", "The last field.");
        Assert(LineDelayQueue.Field("9|Ob", 2) == null && LineDelayQueue.Field(null, 0) == null, "A missing field is null.");
    }

    [Test("LineDelayQueue.ClearDropsWaitingBatchesAndPendingWalks")]
    private static void LineDelayQueueClearDropsWaitingBatchesAndPendingWalks()
    {
        LineDelayQueue queue = new LineDelayQueue();
        queue.Add(new[] { "1|Rs|77|1|2|3", "2|Pz|77|1|2|3|0|0|1" }, 1000, 120.0);
        queue.Add(new[] { "3|Ob|42|SetPosition:v|1~2~3" }, 1020, 120.0);
        Assert(queue.Waiting == 2 && queue.WalkPending("77"), "Both batches wait before reset.");
        int dropped = queue.Clear();
        Assert(dropped == 3 && queue.Waiting == 0 && !queue.WalkPending("77") && queue.Next(5000) == null,
            "Clear drops every waiting line and clears pending walkers.");
    }
}
