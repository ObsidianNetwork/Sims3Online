using SimsOnline.Client;
using SimsOnline.DedicatedServer.Protocol;

namespace SimsOnline.DedicatedServer.Tests;

internal static partial class Program
{
    [Test("ClientClaimControl.RestoreWaitsForOwnServerAcknowledgement")]
    private static void ClaimRestoreWaitsForOwnServerAcknowledgement()
    {
        TaskCompletionSource<ClientCommand> sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClientClaimControl control = new() { Send = command => { sent.SetResult(command); return Task.CompletedTask; } };
        Task<string?> reply = Task.Run(() => control.Handle("CLIENT_CLAIM", new[] { "123" }, 2));
        ClientCommand command = sent.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert(command is ClientCommand.ClaimSim { PlayerId: 2, SimObjectId: 123 },
            "Restore must use the normal player-authorized claim command.");
        control.Observe(new ServerEvent.SimClaimed(1, 123), 2);
        Assert(!reply.IsCompleted, "Another player's event cannot acknowledge this player's claim.");
        control.Observe(new ServerEvent.SimClaimed(2, 123), 2);
        Assert(reply.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() == "claims=123", "Reply must report acknowledged ownership.");
        control.Observe(new ServerEvent.SimReleased(2, 123), 2);
        Assert(control.Handle("CLIENT_CLAIMS", Array.Empty<string>(), 2) == "claims=", "Released claims must disappear.");
    }

    [Test("ClientClaimControl.RejectionIsNotOwnership")]
    private static void ClaimRejectionIsNotOwnership()
    {
        ClientClaimControl control = new();
        control.Send = command =>
        {
            control.Observe(new ServerEvent.CommandRejected(command.CommandId, "owned by another player"), 2);
            return Task.CompletedTask;
        };
        string? reply = control.Handle("CLIENT_CLAIM", new[] { "123" }, 2);
        Assert(reply == "claim_error=owned by another player"
            && control.Handle("CLIENT_CLAIMS", Array.Empty<string>(), 2) == "claims=",
            "A refused claim must never be reported as restored.");
    }
}
