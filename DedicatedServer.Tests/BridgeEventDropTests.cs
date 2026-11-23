using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Network;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>Mod events the controller cannot decode: the first of each kind is logged, every one is counted, EVENTS_DROPPED shows the counts.</summary>
internal static partial class Program
{
    private static readonly TimeSpan BridgeEventDropTestWait = TimeSpan.FromSeconds(10);

    [Test("BridgeLineCodec.DroppedModEventLoggedOnceCountedEachTime")]
    private static void BridgeLineCodecDroppedModEventLoggedOnceCountedEachTime()
    {
        List<string> logged = new();
        BridgeEventDrops drops = new(logged.Add);
        string unparsable = "EVENT|InteractionStarted|not-a-number|456";

        for (int i = 0; i < 3; i++)
        {
            Assert(BridgeLineCodec.TryDecodeEvent(unparsable, drops) == null, "A malformed event still decodes to null.");
        }

        Assert(drops.Count("InteractionStarted") == 3, $"Every drop should be counted, got {drops.Count("InteractionStarted")}.");
        Assert(logged.Count == 1, $"Only the first drop of a kind should be logged, got {logged.Count} lines.");
        Assert(logged[0].Contains("InteractionStarted", StringComparison.Ordinal)
            && logged[0].Contains("FormatException", StringComparison.Ordinal)
            && logged[0].Contains("not-a-number", StringComparison.Ordinal),
            "The log line should name the kind, the reason and show the line: " + logged[0]);

        // Too few fields for a known kind, and an InteractionReplayQueued report that does not parse: each kind once.
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|InteractionQueued|123|456", drops) == null, "A short known event decodes to null.");
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|InteractionReplayQueued|" + BridgeLineCodec.Text("not a report"), drops) == null,
            "An unparsable capture report decodes to null.");
        Assert(drops.Count("InteractionQueued") == 1 && drops.Count("InteractionReplayQueued") == 1, "Each kind should be counted on its own.");
        Assert(logged.Count == 3 && logged[1].Contains("too few fields (4 of 10)", StringComparison.Ordinal) && logged[2].Contains("capture report", StringComparison.Ordinal),
            "Each new kind should be logged once with its reason: " + string.Join(" | ", logged));

        // Every kind with an arm counts, PickFailed too: each arm's guard throws, no list of kinds beside them.
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|PickFailed|" + Guid.NewGuid().ToString("N") + "|1", drops) == null, "A short PickFailed decodes to null.");
        Assert(drops.Count("PickFailed") == 1 && logged.Count == 4 && logged[3].Contains("too few fields (4 of 6)", StringComparison.Ordinal),
            "A short PickFailed should be a drop like any other kind: " + string.Join(" | ", logged));

        string longLine = "EVENT|InteractionCompleted|x|" + new string('9', 5000);
        Assert(BridgeLineCodec.TryDecodeEvent(longLine, drops) == null, "A long malformed line decodes to null.");
        Assert(logged.Count == 5 && logged[4].Length < 600, $"The logged sample should be cut short, got {logged[4].Length} characters.");
        Assert(drops.Describe() == "InteractionCompleted:1/InteractionQueued:1/InteractionReplayQueued:1/InteractionStarted:3/PickFailed:1",
            "EVENTS_DROPPED's report should list every dropped kind with its count, got " + drops.Describe());
    }

    [Test("BridgeLineCodec.WellFormedAndUnknownEventsAreNotDropped")]
    private static void BridgeLineCodecWellFormedAndUnknownEventsAreNotDropped()
    {
        List<string> logged = new();
        BridgeEventDrops drops = new(logged.Add);

        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|InteractionStarted|123|456", drops) is ServerEvent.InteractionStarted { ActorObjectId: 123uL },
            "A well-formed event should decode as before.");
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|InteractionQueued|123|456|" + BridgeLineCodec.Text("Terrain.GoHere") + "|789|1|1|2|3", drops)
            is ServerEvent.InteractionQueued,
            "A well-formed InteractionQueued should decode as before.");
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|CaptureFilterDropped|1|" + BridgeLineCodec.Text("Terrain.GoHere"), drops) == null,
            "A kind the controller does not decode is null.");
        Assert(BridgeLineCodec.TryDecodeEvent("OK|" + Guid.NewGuid().ToString("N"), drops) == null, "A non-EVENT line is null.");
        Assert(drops.Describe() == "-" && logged.Count == 0, "Well-formed lines, unknown kinds and non-events are not drops: " + string.Join(" | ", logged));
    }

    [Test("BridgeAdminListener.AnswersEventsDroppedItself")]
    private static void BridgeAdminListenerAnswersEventsDroppedItself() => RunAdminListenerEventsDroppedAsync().GetAwaiter().GetResult();

    private static async Task RunAdminListenerEventsDroppedAsync()
    {
        // Through the codec's shared counts, as the bridge reader decodes a mod line. The listener has no local callback,
        // like a client controller's: the controller answers EVENTS_DROPPED itself, with no game behind it.
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|CancelIntent|not-a-number|1") == null, "A malformed event decodes to null.");
        long expected = BridgeEventDrops.Shared.Count("CancelIntent");

        await using BridgeAdminListener listener = new(new HostBridgeServer("memory:TS3W"), "127.0.0.1:0");
        await listener.StartAsync(CancellationToken.None);
        string[] endpoint = listener.BoundEndpoint!.Split(':');
        using TcpClient client = new();
        await client.ConnectAsync(endpoint[0], int.Parse(endpoint[1], System.Globalization.CultureInfo.InvariantCulture));
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("EVENTS_DROPPED\n"));
        using StreamReader reader = new(stream, Encoding.UTF8);
        string? reply = await reader.ReadLineAsync().WaitAsync(BridgeEventDropTestWait);

        using JsonDocument document = JsonDocument.Parse(reply ?? "{}");
        JsonElement root = document.RootElement;
        Assert(root.GetProperty("ok").GetBoolean() && root.GetProperty("accepted").GetBoolean(), "EVENTS_DROPPED should succeed without a game: " + reply);
        string report = root.GetProperty("report").GetString() ?? string.Empty;
        Assert(expected >= 1 && report.Split('/').Contains("CancelIntent:" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            $"EVENTS_DROPPED should report the controller's drop count ({expected}): {reply}");
    }
}
