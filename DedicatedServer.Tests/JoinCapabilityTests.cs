using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Mirroring;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary>
/// cause 1: session_joined says whether the server streams presentation, so a client that reads it never starts
/// legacy interaction replay (which used to run until the first sim_animation and moved the player's selection).
/// </summary>
internal static partial class Program
{
    [Test("JoinCapability.ServerListsTheStreamOnlyWhenItRuns")]
    private static void JoinCapabilityServerListsTheStreamOnlyWhenItRuns()
    {
        AuthoritativeSession streaming = MakeSession();
        streaming.ConfigurePresentationStream(true);
        ServerEvent.SessionJoined on = JoinedEvent(streaming, 1);
        Assert(on.Capabilities != null && on.Capabilities.SequenceEqual(new[] { ServerCapability.PresentationStream }),
            "A streaming server lists presentation_stream at the join. Got: " + Describe(on.Capabilities));

        AuthoritativeSession quiet = MakeSession();
        quiet.ConfigurePresentationStream(false);
        ServerEvent.SessionJoined off = JoinedEvent(quiet, 1);
        Assert(off.Capabilities == null, "A server without the stream lists nothing. Got: " + Describe(off.Capabilities));
    }

    [Test("JoinCapability.StreamRunsOnlyWithASyncInterval")]
    private static void JoinCapabilityStreamRunsOnlyWithASyncInterval()
    {
        // Program configures the session's stream flag from this, so a server started without the sync broadcaster
        // (which runs the stream) never tells a joiner it streams.
        Assert(SyncBroadcaster.Runs(SessionConfig.FromArgs(new[] { "--sync-broadcast-seconds", "2" })), "An interval starts the broadcaster.");
        Assert(!SyncBroadcaster.Runs(SessionConfig.FromArgs(new[] { "--sync-broadcast-seconds", "0" })), "Zero does not.");
        Assert(!SyncBroadcaster.Runs(SessionConfig.FromArgs(Array.Empty<string>())), "No flag does not.");
    }

    [Test("JoinCapability.CodecCarriesPresentAbsentAndUnknown")]
    private static void JoinCapabilityCodecCarriesPresentAbsentAndUnknown()
    {
        string present = PublicProtocolCodec.EncodeEvent(
            new ServerEvent.SessionJoined(2, "bob", null, new[] { ServerCapability.PresentationStream }));
        Assert(present.Contains("\"capabilities\":[\"presentation_stream\"]", StringComparison.Ordinal),
            "The field goes on the wire as a camelCase list of names. Got: " + present);
        Assert(PublicProtocolCodec.DecodeEvent(present) is ServerEvent.SessionJoined { Capabilities: { } back }
            && ServerCapability.Has(back, ServerCapability.PresentationStream), "Present round-trips. Got: " + present);

        string absent = PublicProtocolCodec.EncodeEvent(new ServerEvent.SessionJoined(2, "bob"));
        Assert(!absent.Contains("capabilities", StringComparison.Ordinal), "No capabilities are left out. Got: " + absent);
        // An older server's line, as it writes it.
        ServerEvent older = PublicProtocolCodec.DecodeEvent("{\"$type\":\"session_joined\",\"playerId\":2,\"displayName\":\"bob\"}");
        Assert(older is ServerEvent.SessionJoined { Capabilities: null }, "Absent decodes as null. Got: " + older);

        // A newer server's line: a name this client does not know, and a field it does not know either.
        ServerEvent newer = PublicProtocolCodec.DecodeEvent(
            "{\"$type\":\"session_joined\",\"playerId\":2,\"displayName\":\"bob\",\"capabilities\":[\"player_cameras\"],\"cameraRate\":30}");
        Assert(newer is ServerEvent.SessionJoined { Capabilities: { } unknown }
            && unknown.SequenceEqual(new[] { "player_cameras" })
            && !ServerCapability.Has(unknown, ServerCapability.PresentationStream),
            "An unknown name decodes and does not count as the stream. Got: " + newer);
    }

    [Test("JoinCapability.ClientNeverStartsReplayWhenTheServerStreams")]
    private static void JoinCapabilityClientNeverStartsReplayWhenTheServerStreams()
    {
        ServerEvent.InteractionQueued npcGoHere = new(100uL, 4495uL, "Terrain.GoHere", 7uL, new Vector3Dto(1f, 0f, 1f));

        FakeMirrorHost streamedHost = new FakeMirrorHost();
        MirrorEngine streamed = new MirrorEngine(streamedHost);
        streamed.DispatchAsync(new ServerEvent.SessionJoined(2, "bob", null, new[] { ServerCapability.PresentationStream }), CancellationToken.None)
            .GetAwaiter().GetResult();
        DispatchResult dropped = streamed.DispatchAsync(npcGoHere, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!streamed.InteractionReplayEnabled && dropped.Translated == 0 && streamedHost.InteractionQueueCalls.Count == 0,
            $"present: replay is off from the join, before any sim_animation. enabled={streamed.InteractionReplayEnabled} queued={streamedHost.InteractionQueueCalls.Count}");

        FakeMirrorHost olderHost = new FakeMirrorHost();
        MirrorEngine older = new MirrorEngine(olderHost);
        older.DispatchAsync(new ServerEvent.SessionJoined(2, "bob"), CancellationToken.None).GetAwaiter().GetResult();
        older.DispatchAsync(npcGoHere, CancellationToken.None).GetAwaiter().GetResult();
        Assert(older.InteractionReplayEnabled && olderHost.InteractionQueueCalls.Count == 1,
            $"absent: an older server keeps today's replay. enabled={older.InteractionReplayEnabled} queued={olderHost.InteractionQueueCalls.Count}");

        FakeMirrorHost unknownHost = new FakeMirrorHost();
        MirrorEngine unknown = new MirrorEngine(unknownHost);
        unknown.DispatchAsync(new ServerEvent.SessionJoined(2, "bob", null, new[] { "player_cameras" }), CancellationToken.None)
            .GetAwaiter().GetResult();
        unknown.DispatchAsync(npcGoHere, CancellationToken.None).GetAwaiter().GetResult();
        Assert(unknown.InteractionReplayEnabled && unknownHost.InteractionQueueCalls.Count == 1,
            $"unknown only: replay stays as today. enabled={unknown.InteractionReplayEnabled} queued={unknownHost.InteractionQueueCalls.Count}");
    }

    private static ServerEvent.SessionJoined JoinedEvent(AuthoritativeSession session, int playerId)
    {
        CommandResult result = session.HandleCommandAsync(
            new ClientCommand.JoinSession(Guid.NewGuid(), playerId, "p" + playerId), CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.IsAccepted && result.Events.Count > 0 && result.Events[0] is ServerEvent.SessionJoined,
            "The join should be accepted with SessionJoined first. Got: " + result.RejectionReason);
        return (ServerEvent.SessionJoined)result.Events[0];
    }

    private static string Describe(IReadOnlyList<string>? capabilities) =>
        capabilities == null ? "null" : "[" + string.Join(",", capabilities) + "]";
}
