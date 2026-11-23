using System.Reflection;
using SimsOnline.DedicatedServer.Authority;
using SimsOnline.DedicatedServer.HostBridge;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

/// <summary> n14: a client's HUD leave of a mirrored conversation reaches the server as leave_conversation,
/// owner-gated, and becomes the bridge's LEAVE_CONVERSATION.</summary>
internal static partial class Program
{
    [Test("BridgeLineCodec.LeaveConversationIntentDecodes")]
    private static void BridgeLineCodecLeaveConversationIntentDecodes()
    {
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|LeaveConversationIntent|18031287978496800304") is ServerEvent.LeaveConversationIntentCaptured leave
            && leave.ActorObjectId == 18031287978496800304uL
            && PublicProtocolCodec.DecodeEvent(PublicProtocolCodec.EncodeEvent(leave)) is ServerEvent.LeaveConversationIntentCaptured back && back == leave,
            "EVENT|LeaveConversationIntent decodes and round-trips through the public protocol.");

        List<string> logged = new();
        BridgeEventDrops drops = new(logged.Add);
        Assert(BridgeLineCodec.TryDecodeEvent("EVENT|LeaveConversationIntent|not-a-number", drops) == null, "A malformed leave intent decodes to null.");
        Assert(drops.Count("LeaveConversationIntent") == 1, $"The malformed leave intent should be counted, got {drops.Count("LeaveConversationIntent")}.");
    }

    [Test("BridgeLineCodec.LeaveConversationEncodes")]
    private static void BridgeLineCodecLeaveConversationEncodes()
    {
        Guid guid = Guid.NewGuid();
        Assert(BridgeLineCodec.EncodeCommand(new BridgeCommand.LeaveConversation(guid, 100uL)) == $"LEAVE_CONVERSATION|{guid:N}|100", "LeaveConversation should encode.");
        Assert(BridgeLineCodec.KnownCommands.Any(entry => entry.StartsWith("LEAVE_CONVERSATION", StringComparison.Ordinal)), "KnownCommands should advertise LEAVE_CONVERSATION.");
    }

    [Test("PublicProtocolCodec.RoundTripsLeaveConversation")]
    private static void PublicProtocolCodecRoundTripsLeaveConversation()
    {
        ClientCommand.LeaveConversation command = new(Guid.NewGuid(), 2, 500uL);
        string json = PublicProtocolCodec.EncodeCommand(command);
        Assert(json.Contains("\"$type\":\"leave_conversation\"", StringComparison.Ordinal), "The wire discriminator should be leave_conversation: " + json);
        Assert(PublicProtocolCodec.DecodeCommand(json) is ClientCommand.LeaveConversation back && back == command, "LeaveConversation should round-trip.");
    }

    [Test("AuthoritativeSession.LeaveConversationRejectsUnjoined")]
    private static void AuthoritativeSessionLeaveConversationRejectsUnjoined()
    {
        AuthoritativeSession session = MakeSession();
        CommandResult result = session.HandleCommandAsync(new ClientCommand.LeaveConversation(Guid.NewGuid(), 1, 500uL), CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.IsAccepted, "An unjoined player cannot leave a conversation.");
    }

    [Test("AuthoritativeSession.LeaveConversationRejectsForOtherPlayerClaim")]
    private static void AuthoritativeSessionLeaveConversationRejectsForOtherPlayerClaim()
    {
        AuthoritativeSession session = MakeSession();
        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 1, "alice"), CancellationToken.None).GetAwaiter().GetResult();
        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 2, "bob"), CancellationToken.None).GetAwaiter().GetResult();
        session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 500uL), CancellationToken.None).GetAwaiter().GetResult();
        CommandResult result = session.HandleCommandAsync(new ClientCommand.LeaveConversation(Guid.NewGuid(), 2, 500uL), CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.IsAccepted, "A non-owner's LeaveConversation must be rejected.");
        Assert(result.RejectionReason != null && result.RejectionReason.Contains("claimed by player 1", StringComparison.Ordinal), "Rejection reason should name the owner. Got: " + result.RejectionReason);
    }

    [Test("AuthoritativeSession.LeaveConversationAcceptsForOwner")]
    private static void AuthoritativeSessionLeaveConversationAcceptsForOwner()
    {
        AuthoritativeSession session = MakeSession();
        session.HandleCommandAsync(new ClientCommand.JoinSession(Guid.NewGuid(), 1, "alice"), CancellationToken.None).GetAwaiter().GetResult();
        session.HandleCommandAsync(new ClientCommand.ClaimSim(Guid.NewGuid(), 1, 500uL), CancellationToken.None).GetAwaiter().GetResult();
        CommandResult result = session.HandleCommandAsync(new ClientCommand.LeaveConversation(Guid.NewGuid(), 1, 500uL), CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.IsAccepted, "The owner's LeaveConversation should reach the host. Reason: " + result.RejectionReason);
    }

    [Test("HeadlessSims3Host.LeaveConversationMapsToBridge")]
    private static void HeadlessSims3HostLeaveConversationMapsToBridge()
    {
        // The host writes LEAVE_CONVERSATION into the bridge (the pinned region answers nothing, so it then waits).
        HeadlessSims3Host host = new(SessionConfig.FromArgs(Array.Empty<string>()));
        (typeof(HeadlessSims3Host).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_started reflection target was not found.")).SetValue(host, true);
        using PinnedHostBridge fixture = new(host.Bridge);
        Guid id = Guid.NewGuid();
        Task<CommandResult> apply = host.ApplyCommandAsync(new ClientCommand.LeaveConversation(id, 1, 500uL), CancellationToken.None);
        Assert(fixture.Writes.Count == 1 && fixture.Writes.Last().Contains($" LEAVE_CONVERSATION|{id:N}|500", StringComparison.Ordinal),
            "LeaveConversation should be written as LEAVE_CONVERSATION. Writes: " + string.Join(" | ", fixture.Writes));

        Assert(host.DisposeAsync().AsTask().Wait(HostBridgeShutdownWait), "HeadlessSims3Host.DisposeAsync should finish.");
        AssertSendCancelled(apply, "The LeaveConversation cancelled by shutdown");
    }
}
